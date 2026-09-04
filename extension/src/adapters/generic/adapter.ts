import type { AdapterHealth, AdapterReading, SiteAdapter } from '../site-adapter.js';
import { analyze, relevantLetters } from '../../shared/text-normalizer.js';
import type { LetterCounts } from '../../shared/text-normalizer.js';
import { fieldIdentity } from './field-identity.js';

/**
 * Reads any site that is not worth a hand-written adapter, which is nearly all of them.
 *
 * The WhatsApp adapter exists because WhatsApp has a conversation to read: a history, with two
 * people in it, and a direction per message. An ordinary page has none of that. What it has is a
 * box the user types in, and that turns out to be enough, because the strongest evidence this
 * product uses was never message analysis - it is the layout the user actually types with, which
 * the decision engine records through the typing guard and replays from memory on the next visit.
 * That path needs no history at all.
 *
 * So this adapter is deliberately much smaller than the WhatsApp one. It answers three questions:
 * which box, is it empty, and - only until memory has an answer - what language is this page
 * written in.
 *
 * Cost discipline is the other half of the design. A content script that runs on arbitrary sites
 * cannot afford WhatsApp's approach of observing a subtree: `observationRoot` returns null on
 * purpose, so nothing here reacts to a page mutating. Focus and typing are the only triggers, and
 * they are the only moments that matter.
 */

/** Enough text to judge by. Beyond this the answer stops changing and the cost does not. */
const SAMPLE_CHAR_LIMIT = 4_000;

/**
 * How many letters a container needs before it counts as the field's context.
 *
 * Letters, not characters, and this is the second attempt. Counting characters at a bar of 200
 * looked reasonable and failed on the case it was written for: clicking Reply in Gmail opens a
 * compose area whose recipient fields, signature and toolbar clear 200 characters on their own, so
 * the climb stopped inside it and reported `he=0 en=152` for a Hebrew email sitting directly above.
 * It found the interface and never reached the message.
 *
 * Letters are the right unit because they are what the decision is made of, and the bar is set
 * where the evidence starts being worth something: a toolbar produces a hundred or so, a message
 * produces several hundred. Below it, climbing one more level costs nothing.
 */
const MIN_CONTEXT_LETTERS = 200;

/**
 * How much visible text there must be before this adapter claims to have read anything.
 *
 * Confidence is a ratio, so a handful of letters that all belong to one alphabet is reported as
 * certainty. The detector already guards against that with MinWeightedLetters, but five letters is
 * the right bar for a chat message and far too low for a page sample: a spreadsheet whose grid is a
 * canvas yielded 29 visible letters of leftover interface and the engine switched to English at
 * confidence 1.00, four separate times, while the user was typing Hebrew in a cell.
 *
 * Note where this sits. Climbing to a context already requires MIN_CONTEXT_LETTERS, but falling
 * back to the body had no floor at all - it took whatever was there. This is that missing floor,
 * and it is the difference between weak evidence and none. Below it the adapter reports no
 * messages, the engine's global default is all that is left, and the layout is left alone.
 *
 * Calibrated against measured pages rather than chosen: 29 letters on a Google Sheet is noise,
 * while a small real page measured 101, a Gmail thread 326 to 1497, and a search page 3247. The
 * bar sits with margin on both sides of that gap, and it is the kind of number that should be
 * re-measured rather than trusted.
 */
const MIN_EVIDENCE_LETTERS = 60;

/**
 * Elements that carry an application rather than its content.
 *
 * This is the part that actually separates a message from the interface around it, and tuning a
 * number never would have. Gmail's compose area is recipient fields, a formatting toolbar and a
 * Send button; the email above it is prose. Both are text, and by character count the interface
 * wins - which is exactly how a Hebrew email came back as `he=0 en=152`.
 *
 * Chrome is defined structurally: a control, a label for a control, or a landmark whose job is
 * navigation. What is left is what somebody wrote, which is the only thing that says anything
 * about the language the user is about to write in.
 */
const CHROME_TAGS = new Set([
  'BUTTON', 'SELECT', 'OPTION', 'OPTGROUP', 'LABEL', 'LEGEND',
  'NAV', 'HEADER', 'FOOTER', 'ASIDE',
  'INPUT', 'TEXTAREA', 'SUMMARY',
]);

const CHROME_ROLES = new Set([
  'button', 'checkbox', 'radio', 'switch', 'tab', 'tablist', 'toolbar', 'menu', 'menubar',
  'menuitem', 'menuitemcheckbox', 'menuitemradio', 'navigation', 'banner', 'complementary',
  'search', 'listbox', 'option', 'combobox', 'progressbar', 'status', 'tooltip',
]);

/** Structural text that is never evidence: markup that is not rendered as prose. */
const NON_TEXT_TAGS = new Set(['SCRIPT', 'STYLE', 'NOSCRIPT', 'TEMPLATE', 'TITLE']);

/** A second bound, for pages that hold their text in thousands of tiny nodes. */
const SAMPLE_NODE_LIMIT = 600;

/**
 * The page's language is not going to change while the user sits on it, and read() is called on
 * every focus and every keystroke. Sampling each time would be the one genuinely expensive thing
 * this adapter does.
 */
const SAMPLE_TTL_MS = 10_000;

const TEXTUAL_INPUT_TYPES = new Set(['', 'text', 'search', 'email', 'tel', 'url', 'number']);

/**
 * Fields this adapter must never read, count or report.
 *
 * A password is the obvious one. The rest are the fields a password manager fills: card numbers
 * and one-time codes. There is no plausible language signal in any of them, so excluding them
 * costs nothing, and reading them - even to count letters and throw the text away - is not a thing
 * this product should be doing at all. It fails closed: a field that looks sensitive is left alone.
 */
const SENSITIVE_AUTOCOMPLETE = /cc-|credit|card-number|one-time-code|otp|password/i;

function isSensitive(element: Element): boolean {
  if (element instanceof HTMLInputElement && element.type.toLowerCase() === 'password') return true;

  const autocomplete = element.getAttribute('autocomplete');
  if (autocomplete && SENSITIVE_AUTOCOMPLETE.test(autocomplete)) return true;

  // Some sites spell the intent in the name or id instead of autocomplete.
  const name = `${element.getAttribute('name') ?? ''} ${element.getAttribute('id') ?? ''}`;
  return SENSITIVE_AUTOCOMPLETE.test(name);
}

/**
 * True for an element that is or inherits contenteditable.
 *
 * The `isContentEditable` property is the right answer and is used when it exists, but jsdom does
 * not implement it, so the tests would silently exercise a different path than the browser does.
 * Walking the attribute is correct in both, and is needed anyway: a nested editable inherits the
 * attribute from an ancestor rather than carrying one of its own.
 */
function isContentEditable(element: Element): boolean {
  if ((element as HTMLElement).isContentEditable === true) return true;

  let node: Element | null = element;
  while (node) {
    const value = node.getAttribute('contenteditable');
    if (value !== null) return value !== 'false';
    node = node.parentElement;
  }
  return false;
}

function isWritingField(element: Element): boolean {
  if (element instanceof HTMLTextAreaElement) return true;
  if (element instanceof HTMLInputElement) {
    return TEXTUAL_INPUT_TYPES.has(element.type.toLowerCase());
  }
  return isContentEditable(element);
}

/**
 * True for text the user can actually see.
 *
 * The rule this encodes is the whole of it: the language somebody is about to write is informed by
 * what is in front of them. A closed menu, a hidden banner and a screen-reader-only hint are not.
 *
 * A TreeWalker reads text inside `display:none`, and on Google Sheets that turned out to be all
 * there was. Measured on a live blank spreadsheet, the sampler collected 333 characters and every
 * one of them was invisible: "A browser error has occurred", "Turn on screen reader support", and
 * the account panel - which is where the user's own name and email address were being counted from.
 * Filtered on visibility the same page yields 13 characters, far below any bar, so the product
 * declines to decide instead of reading Google's English interface as the language of a
 * spreadsheet.
 *
 * `checkVisibility` accounts for ancestors, so only the text node's own parent needs asking. Where
 * it does not exist - jsdom, and engines older than this extension supports - everything counts,
 * which is the behaviour that came before.
 */
function isVisible(element: Element): boolean {
  const check = (element as { checkVisibility?: (options?: unknown) => boolean }).checkVisibility;
  if (typeof check !== 'function') return true;

  return check.call(element, { checkOpacity: true, checkVisibilityCSS: true });
}

/** True for a node that belongs to the application's furniture rather than its content. */
function isChrome(element: Element): boolean {
  if (CHROME_TAGS.has(element.tagName)) return true;

  const role = element.getAttribute('role');
  return role !== null && CHROME_ROLES.has(role.toLowerCase());
}

/**
 * The prose inside a container: what a person wrote, with the application stripped out.
 *
 * One walker serves both the search for a context and the sample taken from it, so the text that
 * decides which container to read is the same text that is then read. They were two different
 * measurements before, and disagreeing about what counts is how the interface came to outvote the
 * message.
 */
function collectText(container: Element, exclude: Element, charLimit: number, nodeLimit: number): string {
  const parts: string[] = [];
  let total = 0;
  let visited = 0;

  const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT);

  // Many text nodes share a parent, and asking about visibility costs a layout. Once per element
  // is enough.
  const seen = new Map<Element, boolean>();

  while (total < charLimit && visited < nodeLimit) {
    const node = walker.nextNode();
    if (!node) break;
    visited++;

    const value = node.nodeValue?.trim();
    if (!value) continue;

    const parent = node.parentElement;
    if (!parent) continue;

    let visible = seen.get(parent);
    if (visible === undefined) {
      visible = isVisible(parent);
      seen.set(parent, visible);
    }
    if (!visible) continue;

    let skip = false;
    for (let el: Element | null = parent; el && el !== container.parentElement; el = el.parentElement) {
      // The user's own typing, here or in any other box on the page. Counting it would smuggle
      // their words in through the one channel meant to carry somebody else's.
      if (el === exclude || isContentEditable(el) || NON_TEXT_TAGS.has(el.tagName) || isChrome(el)) {
        skip = true;
        break;
      }
    }
    if (skip) continue;

    parts.push(value);
    total += value.length;
  }

  return parts.join(' ').slice(0, charLimit);
}

function valueOf(element: Element): string {
  if (element instanceof HTMLTextAreaElement || element instanceof HTMLInputElement) {
    return element.value;
  }
  return element.textContent ?? '';
}

export class GenericAdapter implements SiteAdapter {
  /** Per-site, so the Agent's existing per-site pause applies to each site separately. */
  get site(): string {
    return window.location.hostname;
  }

  readonly version = '1.0.0';

  private sample: { key: string; at: number; text: string } | null = null;

  /**
   * What was already in the current field when it was first seen, so that "empty" can mean what
   * the typing guard needs it to mean.
   *
   * A WhatsApp composer is genuinely empty until the user types, and reading emptiness as "no
   * characters" was correct there. Anywhere else it is not: a mail client opens a reply box with a
   * signature in it, often a quoted original too, and by that reading the box is occupied from the
   * moment it appears. The guard then suppresses every decision for that message - permanently,
   * because nothing will ever remove the signature. A live log showed exactly that: one thread
   * whose only entry was `Suppressed UserTyping` and which therefore never switched at all.
   *
   * So emptiness is measured against what the field started with rather than against nothing. The
   * user has written when the box differs from how they found it, and clearing back to that state
   * releases the guard again - which is also what should happen after a message is sent.
   */
  private draft: { field: Element; identity: string; baseline: string } | null = null;

  /** Last in the chain, after every site that has an adapter of its own. */
  matches(): boolean {
    return true;
  }

  /**
   * Null on purpose, and the single most important line in this file.
   *
   * Returning an element here would attach a MutationObserver to an arbitrary page, on every site
   * the user enables, for as long as the tab is open. ContentObserver already treats a null root
   * as "nothing to observe" and falls back to focus, visibility and typing - which is not a
   * degraded mode here but the correct one.
   */
  observationRoot(): Element | null {
    return null;
  }

  read(): AdapterReading {
    const active = document.activeElement;

    // Nothing focused, or focus is on a button or a link. There is no field to have an opinion
    // about, so this reports the same "nothing to see" the WhatsApp adapter reports on its landing
    // screen, and ContentObserver returns without sending anything.
    if (!(active instanceof HTMLElement) || !isWritingField(active) || isSensitive(active)) {
      return { rawConversationId: null, messages: [], composerEmpty: true };
    }

    const identity = fieldIdentity(window.location, active);
    const empty = this.userHasNotWritten(active, identity);

    // The user's own draft is deliberately not reported as an outgoing message.
    //
    // It could never reach a decision: a non-empty field means composerEmpty is false, and the
    // engine's typing guard suppresses and learns before it ever looks at messages. Sending it
    // anyway would put a value on the wire that nothing reads, and the next person to look would
    // reasonably assume it was doing something.
    const counts = empty ? this.pageLanguageCounts(active) : {};

    return {
      rawConversationId: identity,
      messages:
        relevantLetters(counts) >= MIN_EVIDENCE_LETTERS
          ? // Incoming, because that is what this is: words the user did not write. The engine
            // holds incoming evidence to a higher bar before acting on it, and refuses to commit
            // it to memory - precisely the treatment a page's own language deserves. Both rules
            // already exist and were paid for once: a previous version of this product learned
            // from the other side's language and got steadily worse until the store was cleared.
            [{ direction: 'incoming' as const, counts, index: 0 }]
          : // Not enough visible text to mean anything. Reporting no messages at all - rather than
            // a thin one - is what lets the engine's global default apply, since it only fires
            // when nothing whatsoever was observed, and what stops a ratio over a few letters
            // being handed back as certainty.
            [],
      composerEmpty: empty,
    };
  }

  /**
   * Whether the user has added anything to this field since they arrived at it.
   *
   * See `draft` above for why this is not the same question as whether the field holds characters.
   * The baseline is captured the first time a field is seen and kept until a different field takes
   * over, so leaving the tab and coming back to a half-written message still reads as writing -
   * the guard must not release just because attention moved.
   */
  private userHasNotWritten(field: Element, identity: string): boolean {
    const text = valueOf(field).trim();

    // Reset on the identity as well as on the element. A single-page application can keep one
    // compose box and swap the conversation behind it, and a baseline held over from the previous
    // message would make the new one look written-in from the moment it opened - the same lock the
    // signature caused, arriving by a different door.
    if (!this.draft || this.draft.field !== field || this.draft.identity !== identity) {
      this.draft = { field, identity, baseline: text };
    }

    return text === this.draft.baseline;
  }

  /**
   * The smallest container around the writing field that holds real text.
   *
   * Sampling the whole document was the first version of this, and it was wrong in a way that only
   * a real application shows. In Gmail the interface is English - menus, labels, buttons, the rest
   * of the list - and one Hebrew email inside it does not outweigh that. The live log read
   * `he=1228 en=1642` on exactly that page, a confidence of about 0.57 against a bar of 0.85, so
   * the engine correctly refused to decide and the product did nothing. The Hebrew was found and
   * then drowned.
   *
   * What matters for "which language am I about to write here" is the thread this box belongs to,
   * not the navigation around it. So this climbs from the field until it finds a container with
   * enough text of its own, which on a mail client is the message, on a forum the post, and on a
   * plain page the body - the old behaviour, reached only when nothing narrower qualifies.
   */
  private contextFor(field: Element): Element | null {
    let node = field.parentElement;

    while (node && node !== document.body) {
      const prose = collectText(node, field, SAMPLE_CHAR_LIMIT, SAMPLE_NODE_LIMIT);
      if (relevantLetters(analyze(prose)) >= MIN_CONTEXT_LETTERS) return node;
      node = node.parentElement;
    }

    return document.body;
  }

  /**
   * What language the text around this field is written in, as letter counts.
   *
   * Sampled rather than read in full, and cached, because this runs whenever the box is empty.
   * Text inside editable elements is skipped: that is the user's own typing, and counting it here
   * would quietly smuggle their words in through the one channel meant to carry the site's.
   */
  private pageLanguageCounts(exclude: Element): LetterCounts {
    const context = this.contextFor(exclude);
    if (!context) return {};

    // The context's size is part of the key, not just the address. Mail clients and chat apps
    // swap one thread for another without touching the URL, and a cache keyed on the address
    // alone would answer for the previous conversation for another ten seconds.
    const key = `${window.location.href}|${document.title}|${context.textContent?.length ?? 0}`;
    const now = Date.now();

    if (this.sample && this.sample.key === key && now - this.sample.at < SAMPLE_TTL_MS) {
      return analyze(this.sample.text);
    }

    const text = this.samplePageText(context, exclude);
    this.sample = { key, at: now, text };
    return analyze(text);
  }

  private samplePageText(context: Element, exclude: Element): string {
    const prose = collectText(context, exclude, SAMPLE_CHAR_LIMIT, SAMPLE_NODE_LIMIT);

    // The title is short and almost always in the site's own language - useful when the context is
    // the whole body, misleading when it is one message inside an application whose interface is
    // in another language. So it is used only in the case it was meant for.
    if (context === document.body && document.title) {
      return `${document.title} ${prose}`.slice(0, SAMPLE_CHAR_LIMIT);
    }

    return prose;
  }

  /**
   * Always healthy.
   *
   * There is nothing here to break. Health exists because the WhatsApp adapter depends on
   * selectors that WhatsApp changes without warning; this adapter depends on `activeElement` and
   * the shape of a form control, which are the parts of the platform that do not move. Reporting
   * anything else would put a warning in front of the user that no action could clear.
   */
  checkHealth(): AdapterHealth {
    return { healthy: true, missing: [], tiers: {} };
  }
}
