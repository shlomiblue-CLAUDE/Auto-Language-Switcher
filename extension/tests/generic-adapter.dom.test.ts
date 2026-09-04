import { beforeEach, describe, expect, it } from 'vitest';
import { GenericAdapter } from '../src/adapters/generic/adapter.js';
import { describeField, documentScope, fieldIdentity } from '../src/adapters/generic/field-identity.js';
import { relevantLetters } from '../src/shared/text-normalizer.js';

/**
 * The generic adapter is what makes this product work anywhere the user types, and it has to be
 * held to a stricter standard than the WhatsApp one for exactly that reason: it runs on sites
 * nobody has looked at, including sites with a password box on them.
 *
 * Two properties matter more than the rest, and both have a test that fails loudly:
 *
 *  - a sensitive field produces no identity, no counts and no signal at all;
 *  - the same box yields the same identity on the next page load, because an identity that churns
 *    means a memory that never accumulates, and that failure is silent.
 */

const HEBREW = 'שלום וברוך הבא לאתר שלנו';
const ENGLISH = 'Welcome to our site, please sign in below';

/**
 * Page fixtures have to carry real weight now.
 *
 * The adapter refuses to report evidence below MIN_EVIDENCE_LETTERS, because a ratio over a few
 * letters is returned as certainty - which is how 29 letters of leftover interface on a Google
 * Sheet switched the layout to English while the user typed Hebrew. A fixture with one short
 * sentence in it would be testing the floor rather than the behaviour it was written for.
 */
const HEBREW_PAGE = `${HEBREW} `.repeat(5);
const ENGLISH_PAGE = `${ENGLISH} `.repeat(5);

function field(html: string): HTMLElement {
  document.body.innerHTML = html;
  const element = document.querySelector<HTMLElement>('[data-subject]');
  if (!element) throw new Error('fixture has no [data-subject]');
  element.focus();
  return element;
}

describe('GenericAdapter', () => {
  let adapter: GenericAdapter;

  beforeEach(() => {
    adapter = new GenericAdapter();
    document.body.innerHTML = '';
    document.title = '';
  });

  describe('what counts as a writing field', () => {
    it('reads a textarea, an input and a contenteditable', () => {
      for (const html of [
        '<textarea data-subject name="body"></textarea>',
        '<input data-subject name="q">',
        '<div data-subject contenteditable="true" aria-label="Message"></div>',
      ]) {
        field(html);
        expect(adapter.read().rawConversationId).not.toBeNull();
      }
    });

    it('ignores a page where nothing is focused', () => {
      document.body.innerHTML = '<p>nothing to type in here</p>';
      const reading = adapter.read();

      expect(reading.rawConversationId).toBeNull();
      expect(reading.messages).toEqual([]);
      // Empty, not "occupied": the WhatsApp adapter fails closed on a missing composer because a
      // missing composer means it lost the page. Here there is simply no field, which is the
      // ordinary state of most pages and must not read as the user typing.
      expect(reading.composerEmpty).toBe(true);
    });

    it('ignores a button, a link and a checkbox', () => {
      for (const html of [
        '<button data-subject>Send</button>',
        '<a data-subject href="#">Home</a>',
        '<input data-subject type="checkbox">',
      ]) {
        field(html);
        expect(adapter.read().rawConversationId).toBeNull();
      }
    });

    it('treats contenteditable=false as not editable', () => {
      field('<div data-subject contenteditable="false" tabindex="0"></div>');
      expect(adapter.read().rawConversationId).toBeNull();
    });

    it('follows contenteditable inherited from an ancestor', () => {
      document.body.innerHTML =
        '<div contenteditable="true"><p data-subject tabindex="0">draft</p></div>';
      const element = document.querySelector<HTMLElement>('[data-subject]')!;
      element.focus();

      expect(adapter.read().rawConversationId).not.toBeNull();
    });
  });

  describe('fields it must never touch', () => {
    it('produces nothing at all for a password box', () => {
      document.title = HEBREW;
      field('<input data-subject type="password" name="password">');

      const reading = adapter.read();

      // No identity means ContentObserver returns before it hashes, before it builds a signal and
      // before it sends anything. Nothing about this field reaches the service worker.
      expect(reading.rawConversationId).toBeNull();
      expect(reading.messages).toEqual([]);
    });

    it('produces nothing for card and one-time-code fields', () => {
      for (const html of [
        '<input data-subject autocomplete="cc-number">',
        '<input data-subject autocomplete="one-time-code">',
        '<input data-subject name="card-number">',
        '<input data-subject id="otp">',
      ]) {
        field(html);
        expect(adapter.read().rawConversationId).toBeNull();
      }
    });
  });

  describe('the page as evidence', () => {
    it('reports the page language as incoming, never as the user', () => {
      document.title = 'ברוכים הבאים';
      field('<textarea data-subject name="comment"></textarea>');
      document.body.insertAdjacentHTML('beforeend', `<p>${HEBREW_PAGE}</p>`);

      const [message, ...rest] = adapter.read().messages;

      expect(rest).toEqual([]);
      // Incoming is the whole point. The engine holds incoming evidence to a higher bar and
      // refuses to write it to memory, which is the treatment a page's own words deserve: acting
      // on them once is a reasonable guess, remembering them is how the product taught itself the
      // other side's language and got worse over thirty conversations.
      expect(message?.direction).toBe('incoming');
      expect(message?.counts.Hebrew ?? 0).toBeGreaterThan(0);
    });

    it('does not count the user’s own draft as the page', () => {
      const box = field('<textarea data-subject name="comment"></textarea>') as HTMLTextAreaElement;
      document.body.insertAdjacentHTML('beforeend', `<p>${ENGLISH_PAGE}</p>`);

      adapter.read();
      box.value = HEBREW;

      const reading = adapter.read();

      // Once the user has written, this is the typing guard's business, and the guard fires before
      // the engine looks at any message. Sending evidence here would be sending something nothing
      // reads.
      expect(reading.composerEmpty).toBe(false);
      expect(reading.messages).toEqual([]);
    });

    it('excludes text the user typed elsewhere on the page', () => {
      field('<textarea data-subject name="comment"></textarea>');
      document.body.insertAdjacentHTML(
        'beforeend',
        `<p>${ENGLISH_PAGE}</p><div contenteditable="true">${HEBREW_PAGE}</div>`,
      );

      const counts = adapter.read().messages[0]?.counts ?? {};

      // The Hebrew on this page is the user's, sitting in another editable box. Counting it would
      // smuggle their words in through the channel meant to carry the site's.
      expect(counts.Hebrew ?? 0).toBe(0);
      expect(counts.English ?? 0).toBeGreaterThan(0);
    });

    it('does not read a page as English because of its JavaScript', () => {
      document.title = 'ברוכים הבאים';
      field('<textarea data-subject name="comment"></textarea>');
      document.body.insertAdjacentHTML('beforeend', `<p>${HEBREW_PAGE}</p>`);
      document.body.insertAdjacentHTML(
        'beforeend',
        '<script>const translate = function veryLongEnglishIdentifier() { return "hello world"; };</script>' +
          '<style>.button { background: white; font-family: Helvetica; }</style>',
      );

      const counts = adapter.read().messages[0]?.counts ?? {};

      expect(counts.English ?? 0).toBe(0);
      expect(counts.Hebrew ?? 0).toBeGreaterThan(0);
    });

    it('reads the thread around the box, not the application around the thread', () => {
      // The failure this exists for, taken from a live log rather than imagined: a Hebrew email
      // opened in Gmail produced `he=1228 en=1642` - about 0.57 confidence against a bar of 0.85 -
      // so the engine refused to decide and the product did nothing. The Hebrew was found and then
      // outvoted by the interface around it. Sampling the whole document is what made an English
      // menu bar evidence about the language of a Hebrew message.
      const chrome = 'Inbox Starred Snoozed Sent Drafts More Compose Reply Forward Archive '.repeat(8);
      const email = 'שלום רב, אני מעוניין להמשיך את השיחה שלנו מאתמול בנוגע להצעת המחיר. '.repeat(4);

      document.title = 'Gmail';
      document.body.innerHTML = `
        <nav>${chrome}</nav>
        <aside>${chrome}</aside>
        <main>
          <article>
            <p>${email}</p>
            <div data-subject contenteditable="true" aria-label="Message Body"></div>
          </article>
        </main>`;

      const box = document.querySelector<HTMLElement>('[data-subject]')!;
      box.focus();

      const counts = new GenericAdapter().read().messages[0]?.counts ?? {};

      expect(counts.Hebrew ?? 0).toBeGreaterThan(0);
      // Not merely present - decisive. Anything less and the engine is right to do nothing.
      expect(counts.Hebrew ?? 0).toBeGreaterThan((counts.English ?? 0) * 5);
    });

    it('still falls back to the whole page when nothing narrower has any text', () => {
      document.title = 'ברוכים הבאים';
      document.body.innerHTML =
        `<div><span><textarea data-subject name="q"></textarea></span><p>${HEBREW_PAGE}</p></div>`;
      document.querySelector<HTMLElement>('[data-subject]')!.focus();

      // No container around the field holds enough text to be a context, so the body is the
      // context - which is the original behaviour, and correct for a plain page.
      const counts = new GenericAdapter().read().messages[0]?.counts ?? {};
      expect(counts.Hebrew ?? 0).toBeGreaterThan(0);
    });

    it('does not let a quoted reply count as its own context', () => {
      // A reply box holding the quoted original would otherwise satisfy the text bar at its own
      // parent, stopping the climb immediately and reading the user's draft as the site's language.
      const quote = 'On Tuesday somebody wrote a fairly long message in English which is quoted here. '.repeat(4);
      const thread = 'זהו גוף ההודעה המקורית בעברית שאליה אנחנו משיבים כעת בהמשך לשיחה. '.repeat(4);

      document.body.innerHTML = `
        <main>
          <article><p>${thread}</p></article>
          <section>
            <div data-subject contenteditable="true"><blockquote>${quote}</blockquote></div>
          </section>
        </main>`;

      const box = document.querySelector<HTMLElement>('[data-subject]')!;
      box.focus();

      const reading = new GenericAdapter().read();

      // The quote was there when the box opened, so the user has not written yet and a decision is
      // still owed. What that decision must not rest on is the quote itself: it sits inside the
      // field, and reading it would let the box answer a question about its own surroundings.
      expect(reading.composerEmpty).toBe(true);

      const counts = reading.messages[0]?.counts ?? {};
      expect(counts.Hebrew ?? 0).toBeGreaterThan((counts.English ?? 0) * 5);
    });

    it('counts only what the user can see', () => {
      // Measured on a live blank Google Sheet: the sampler collected 333 characters and every one
      // was invisible - "A browser error has occurred", "Turn on screen reader support", and the
      // account panel, which is where the user's own name and email were being read from. The grid
      // is a canvas, so there was no content at all, and the product read Google's English
      // interface as the language of a spreadsheet at full confidence.
      field('<textarea data-subject name="comment"></textarea>');
      document.body.insertAdjacentHTML(
        'beforeend',
        `<div id="closed-menu">${ENGLISH_PAGE}</div><p id="on-screen">${HEBREW_PAGE}</p>`,
      );

      // jsdom implements no layout, so visibility is stated rather than computed - which is also
      // how the browser answers, just from the cascade instead of from a test.
      const hidden = document.getElementById('closed-menu')!;
      (hidden as unknown as { checkVisibility: () => boolean }).checkVisibility = () => false;

      const counts = adapter.read().messages[0]?.counts ?? {};

      expect(counts.English ?? 0).toBe(0);
      expect(counts.Hebrew ?? 0).toBeGreaterThan(0);
    });

    it('says nothing rather than something thin', () => {
      // The defect, in the numbers the log actually carried. A spreadsheet whose grid is a canvas
      // left 29 visible letters of interface behind; confidence is a ratio, so 29 English letters
      // and no Hebrew read as certainty and the engine switched to English four separate times
      // while the user was typing Hebrew in a cell.
      //
      // The climb to a context already had a bar. Falling back to the body had none, and took
      // whatever was there.
      field('<textarea data-subject name="comment"></textarea>');
      document.body.insertAdjacentHTML('beforeend', '<p>Sheet1 Explore Sum Average Count</p>');

      const reading = adapter.read();

      // No evidence at all, not weak evidence: that is what lets the engine fall through to its
      // global default and leave the keyboard where the user put it.
      expect(reading.messages).toEqual([]);
    });

    it('reports no messages when the page has no letters, so the default can apply', () => {
      field('<textarea data-subject name="comment"></textarea>');
      document.body.insertAdjacentHTML('beforeend', '<p>12 34 56 — 78%</p>');

      // An observation with zero letters is not the same as no observation: the engine's global
      // default only fires when nothing whatsoever was seen, so an empty one would suppress it.
      expect(adapter.read().messages).toEqual([]);
    });
  });

  describe('emptiness', () => {
    it('a reply box that opens with a signature is still owed a decision', () => {
      // The defect, from a live log: one Gmail thread whose only entry was `Suppressed UserTyping`
      // and which therefore never switched at all. A mail client opens a reply box with a
      // signature already in it, so reading emptiness as "no characters" left the typing guard
      // holding that message shut forever - nothing was ever going to remove the signature.
      const box = field(
        '<textarea data-subject name="body">\n\n--\nBest regards, Alice</textarea>',
      ) as HTMLTextAreaElement;

      expect(adapter.read().composerEmpty).toBe(true);

      // And the moment they actually write, the guard closes as it always did.
      box.value = `${box.value}שלום`;
      expect(adapter.read().composerEmpty).toBe(false);
    });

    it('reopens for a decision when the draft is cleared back', () => {
      const box = field('<textarea data-subject name="body">-- Alice</textarea>') as HTMLTextAreaElement;
      adapter.read();

      box.value = '-- Aliceשלום';
      expect(adapter.read().composerEmpty).toBe(false);

      // Sending a message returns the box to how it was found, and a new decision becomes possible
      // again. Without this the guard would latch on the first keystroke and never let go.
      box.value = '-- Alice';
      expect(adapter.read().composerEmpty).toBe(true);
    });

    it('keeps a half-written message guarded across a change of focus', () => {
      const box = field('<textarea data-subject name="body"></textarea>') as HTMLTextAreaElement;
      adapter.read();
      box.value = 'חצי משפט';
      expect(adapter.read().composerEmpty).toBe(false);

      // Leaving the tab and coming back must not read as a fresh box. Attention moved; the
      // half-written sentence did not, and switching under it is the one thing never to do.
      document.body.insertAdjacentHTML('beforeend', '<a href="#" id="away">away</a>');
      document.getElementById('away')!.focus();
      adapter.read();
      box.focus();

      expect(adapter.read().composerEmpty).toBe(false);
    });

    it('is the difference between a decision and the typing guard', () => {
      const empty = field('<textarea data-subject name="body"></textarea>');
      expect(adapter.read().composerEmpty).toBe(true);

      (empty as HTMLTextAreaElement).value = 'ש';
      expect(adapter.read().composerEmpty).toBe(false);
    });

    it('treats whitespace as empty', () => {
      const element = field('<textarea data-subject name="body"></textarea>');
      (element as HTMLTextAreaElement).value = '   \n  ';

      expect(adapter.read().composerEmpty).toBe(true);
    });
  });

  describe('health', () => {
    it('is always healthy, because there is nothing here to break', () => {
      // The WhatsApp adapter reports health because it depends on selectors WhatsApp changes
      // without notice. This one depends on activeElement. A warning the user could never act on
      // would be worse than none.
      expect(adapter.checkHealth()).toEqual({ healthy: true, missing: [], tiers: {} });
    });
  });

  describe('cost', () => {
    it('offers no observation root, so no MutationObserver is ever attached', () => {
      field('<textarea data-subject name="body"></textarea>');

      // This is the line that keeps the product cheap on sites nobody vetted. ContentObserver
      // treats null as "nothing to observe" and falls back to focus, visibility and typing.
      expect(adapter.observationRoot()).toBeNull();
    });
  });
});

describe('field identity', () => {
  beforeEach(() => {
    document.body.innerHTML = '';
  });

  function describeOnly(html: string): string | null {
    document.body.innerHTML = html;
    return describeField(document.querySelector('[data-subject]')!);
  }

  it('prefers the accessible name, then name, then id', () => {
    expect(describeOnly('<textarea data-subject aria-label="Message body" name="b" id="c">')).toBe(
      'aria-label=Message body',
    );
    expect(describeOnly('<textarea data-subject name="b" id="c">')).toBe('name=b');
    expect(describeOnly('<textarea data-subject id="c">')).toBe('id=c');
  });

  it('is stable across a reload of the same page', () => {
    const markup = '<main><form><textarea data-subject placeholder="Write something"></textarea></form></main>';

    const first = describeOnly(markup);
    const second = describeOnly(markup);

    expect(first).toBe(second);
  });

  it('separates two fields on the same page', () => {
    document.body.innerHTML = '<input name="q"><textarea name="comment"></textarea>';

    const search = describeField(document.querySelector('input')!);
    const comment = describeField(document.querySelector('textarea')!);

    expect(search).not.toBe(comment);
  });

  it('refuses identifiers that are minted fresh on every render', () => {
    // React and Radix generate these per mount. Accepting one produces a field whose identity
    // changes on every load, so it learns constantly and remembers nothing - and it fails without
    // any symptom except the product quietly never improving.
    for (const value of [':r7:', 'radix-:r3:', 'a3f9c2e18b4d', '1734']) {
      expect(describeOnly(`<textarea data-subject id="${value}"></textarea>`)).toBeNull();
    }
  });

  it('gives an unnamed box no name of its own', () => {
    // It used to get a bounded path through the tree, which reads as thorough and behaves as
    // churn: the path changes the moment a page wraps something in another element, and an
    // identity that moves learns constantly and remembers nothing.
    expect(describeOnly('<div><section><p><textarea data-subject></textarea></p></section></div>')).toBeNull();
  });

  it('gives every unnamed box in one document the same identity', () => {
    document.body.innerHTML = '<div><textarea></textarea><p><textarea></textarea></p></div>';
    const where = { origin: 'https://docs.example', pathname: '/d/abc', hash: '' };
    const [first, second] = document.querySelectorAll('textarea');

    // Two boxes, different places in the tree, neither carrying a name, one memory. That is the
    // trade: a page with two anonymous boxes shares a language between them, which is a small loss
    // beside a product that learns nothing at all.
    expect(fieldIdentity(where, first!)).toBe(fieldIdentity(where, second!));

    // A box that does have a name still keeps its own.
    document.body.insertAdjacentHTML('beforeend', '<input aria-label="Search">');
    expect(fieldIdentity(where, document.querySelector('input')!)).not.toBe(
      fieldIdentity(where, first!),
    );
  });

  it('separates the same field description on two different sites', () => {
    document.body.innerHTML = '<textarea data-subject name="comment"></textarea>';
    const element = document.querySelector('[data-subject]')!;

    expect(fieldIdentity({ origin: 'https://a.example', pathname: '/', hash: '' }, element)).not.toBe(
      fieldIdentity({ origin: 'https://b.example', pathname: '/', hash: '' }, element),
    );
  });
});

describe('document scope', () => {
  const at = (pathname: string, hash = '') => documentScope({ pathname, hash });

  it('separates one mail thread from another', () => {
    // The defect this exists for, from a live log: every reply box in Gmail carries the same
    // aria-label, so all of Gmail was one remembered language. The key switched to English, then
    // to Hebrew, then back - always from ConversationMemory, never from the message on screen.
    expect(at('/mail/u/0/', '#inbox/FMfcgzAAAA')).not.toBe(at('/mail/u/0/', '#inbox/FMfcgzBBBB'));
  });

  it('treats a spreadsheet as one document however the selection moves', () => {
    // A live Google Sheet reads `#gid=0`: the sheet tab within the file. Every tab of one
    // spreadsheet should therefore share a memory, and a view parameter should never mint a
    // conversation - which is the same rule the query string already follows.
    const sheet = '/spreadsheets/d/1AbC/edit';
    expect(at(sheet, '#gid=0')).toBe(at(sheet, '#gid=3'));
    expect(at(sheet, '#gid=0')).toBe(at(sheet));

    // A different spreadsheet is still a different document.
    expect(at(sheet, '#gid=0')).not.toBe(at('/spreadsheets/d/9XyZ/edit', '#gid=0'));
  });

  it('keeps one search box across different searches', () => {
    // The opposite failure, and the reason the query string is left out. A box that gets a fresh
    // identity per query would learn constantly and remember nothing.
    expect(at('/search')).toBe(at('/search'));
  });

  it('ignores a trailing slash and a bare hash', () => {
    // Both appear and disappear as an application navigates, and neither means a new document.
    expect(at('/inbox/')).toBe(at('/inbox'));
    expect(at('/inbox', '#')).toBe(at('/inbox'));
  });

  it('stays bounded, however long the address is', () => {
    expect(at('/x'.repeat(500), '#y'.repeat(500)).length).toBeLessThanOrEqual(120);
  });
});

describe('the privacy contract holds for arbitrary pages', () => {
  it('never puts text on the wire, only counts', () => {
    document.body.innerHTML = '<textarea data-subject name="comment"></textarea>';
    const element = document.querySelector<HTMLElement>('[data-subject]')!;
    element.focus();
    document.body.insertAdjacentHTML('beforeend', `<p>${HEBREW_PAGE}</p>`);

    const reading = new GenericAdapter().read();
    const serialised = JSON.stringify(reading.messages);

    // The counts must be real - a test that passes because nothing was read proves nothing.
    expect(relevantLetters(reading.messages[0]?.counts ?? {})).toBeGreaterThan(0);
    for (const word of HEBREW.split(' ')) {
      expect(serialised).not.toContain(word);
    }
  });
});
