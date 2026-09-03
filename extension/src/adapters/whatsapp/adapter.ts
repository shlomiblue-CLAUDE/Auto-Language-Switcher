import type {
  AdapterHealth,
  AdapterReading,
  ObservedMessage,
  SiteAdapter,
} from '../site-adapter.js';
import { analyze, relevantLetters } from '../../shared/text-normalizer.js';
import { SELECTORS, resolve, resolveAll, type SelectorSpec } from './selectors.js';
import {
  bubbleOf,
  detectDirection,
  domMeasure,
  panelContext,
  type DirectionEvidence,
  type Measure,
} from './direction.js';

/**
 * Reads WhatsApp Web and produces letter counts.
 *
 * Two constraints shape everything here.
 *
 * Privacy: text is read, counted and discarded inside one function. Nothing that leaves carries a
 * character the user wrote. The only identifying value that even briefly exists is the chat title,
 * returned raw purely so the caller can hash it.
 *
 * Fragility: WhatsApp changes its DOM without notice, and did so between the first version of this
 * file and the second. The adapter therefore reports how well it recognised the page rather than
 * assuming it succeeded — and `checkHealth` now reports whether direction could actually be read,
 * because the previous version passed every structural check while silently understanding nothing.
 */
export class WhatsAppAdapter implements SiteAdapter {
  readonly site = 'web.whatsapp.com';

  /** Bump on any selector change. Surfaced in debug output so a field report identifies the build. */
  readonly version = '2.0.0';

  private readonly measure: Measure;

  /** The measure is injectable because jsdom performs no layout and reports every rect as zero. */
  constructor(measure: Measure = domMeasure) {
    this.measure = measure;
  }

  matches(location: Location): boolean {
    return location.hostname === 'web.whatsapp.com';
  }

  observationRoot(): Element | null {
    // The message list, not document. Observing everything would mean re-reading on every presence
    // tick and timestamp update, which WhatsApp emits constantly.
    return resolve(SELECTORS.messagesPanel).element ?? resolve(SELECTORS.mainPanel).element;
  }

  read(maxMessages: number): AdapterReading {
    const main = resolve(SELECTORS.mainPanel).element;
    if (!main) {
      // No conversation open. Not an error - this is the landing screen.
      return { rawConversationId: null, messages: [], composerEmpty: true };
    }

    const panel = resolve(SELECTORS.messagesPanel).element;
    const { elements: rows } = resolveAll(SELECTORS.messageRow, main);

    // Rendered oldest to newest, so the tail is the recent history that matters.
    const recent = rows.slice(-maxMessages).reverse();

    const messages: ObservedMessage[] = [];

    if (panel) {
      // Measured once. Every bubble is compared against the same frame, and doing it per row would
      // force a layout pass per message.
      const context = panelContext(panel, this.measure);

      for (const row of recent) {
        // A row with no bubble is not a message: date dividers, encryption notices and unread
        // markers are rows too.
        if (!bubbleOf(row)) continue;

        const { direction } = detectDirection(row, context, this.measure);

        // Unknown direction is worse than no data, so the row is dropped rather than guessed.
        if (!direction) continue;

        const counts = analyze(this.textOf(row));
        if (relevantLetters(counts) === 0) continue;

        messages.push({ direction, counts, index: messages.length });
      }
    }

    return {
      rawConversationId: this.conversationIdFrom(main),
      messages,
      composerEmpty: this.isComposerEmpty(),
    };
  }

  /**
   * PDR section 12 typing guard. A missing composer is reported as NOT empty, so an adapter that
   * has lost the page fails closed and suppresses switching rather than switching blindly.
   */
  isComposerEmpty(): boolean {
    const composer = resolve<HTMLElement>(SELECTORS.composer).element;
    if (!composer) return false;
    return (composer.textContent ?? '').trim().length === 0;
  }

  checkHealth(): AdapterHealth {
    const missing: string[] = [];
    const tiers: Record<string, number> = {};

    for (const spec of Object.values(SELECTORS) as SelectorSpec[]) {
      const { tier } = resolve(spec);
      tiers[spec.name] = tier;
      if (tier === -1 && spec.required) missing.push(spec.name);
    }

    if (resolve(SELECTORS.mainPanel).element === null) {
      // No conversation open. With the chat list on screen this is the ordinary landing state:
      // there is nothing to read and nothing wrong. read() has always treated it that way, and
      // health has to agree or the popup accuses WhatsApp of changing every time no chat is open.
      if (this.isChatListVisible()) return { healthy: true, missing: [], tiers };

      // Neither a conversation nor a chat list. That is the sign-in screen, or a WhatsApp this
      // adapter no longer understands - and nothing observable from here separates the two. Saying
      // "WhatsApp changed its page structure" would be a guess, and wrong most of the time, since
      // being signed out is common and a redesign is rare. Reported as its own state so the popup
      // can say what is actually known.
      return { healthy: false, missing: ['signedIn'], tiers };
    }

    // Structure is not comprehension.
    //
    // The previous adapter matched every selector it looked for and still understood nothing,
    // because direction had quietly moved. Health now asks the question that actually matters:
    // on a page with messages on it, can this adapter tell who sent them?
    const evidence = this.directionEvidence();
    if (evidence.rowsWithBubbles > 0 && evidence.resolved === 0) {
      missing.push('messageDirection');
    }

    return { healthy: missing.length === 0, missing, tiers };
  }

  /**
   * True when the chat list is on screen, which is the one reliable sign that WhatsApp is signed in
   * and rendered.
   *
   * Verified against the live page: the chat list is a grid of rows, not the list items an older
   * version of these selectors expected.
   */
  private isChatListVisible(): boolean {
    return (
      document.querySelector('#pane-side') !== null ||
      document.querySelector('[role="grid"] [role="row"]') !== null
    );
  }

  /** How direction was determined across the visible messages. Diagnostic, and a health input. */
  directionEvidence(): {
    rowsWithBubbles: number;
    resolved: number;
    by: Record<DirectionEvidence, number>;
  } {
    const by: Record<DirectionEvidence, number> = {
      geometry: 0,
      'sender-label': 0,
      tail: 0,
      none: 0,
    };

    const main = resolve(SELECTORS.mainPanel).element;
    const panel = resolve(SELECTORS.messagesPanel).element;
    if (!main || !panel) return { rowsWithBubbles: 0, resolved: 0, by };

    const context = panelContext(panel, this.measure);
    const { elements: rows } = resolveAll(SELECTORS.messageRow, main);

    let rowsWithBubbles = 0;
    let resolved = 0;

    for (const row of rows.slice(-10)) {
      if (!bubbleOf(row)) continue;
      rowsWithBubbles++;

      const { direction, evidence } = detectDirection(row, context, this.measure);
      by[evidence]++;
      if (direction) resolved++;
    }

    return { rowsWithBubbles, resolved, by };
  }

  /**
   * Text of one bubble.
   *
   * Falls back to the row's own textContent when no text span matches, which picks up timestamps
   * and status words. Acceptable: those are digits and short strings the letter counter discards,
   * and over-reading is safer than silently reading nothing.
   */
  private textOf(row: Element): string {
    const { elements } = resolveAll(SELECTORS.messageText, row);
    if (elements.length === 0) return row.textContent ?? '';

    return elements.map((element) => element.textContent ?? '').join(' ');
  }

  /**
   * Conversation identity — and a known weakness.
   *
   * This used to come from the chat JID inside `data-id`, which survived renames. That JID is gone
   * from the DOM entirely: as of September 2026 no element anywhere on the page carries one. The
   * only remaining per-conversation identifier is the header title, so renaming a contact orphans
   * their saved preference and the conversation is learned again from scratch.
   *
   * Degrading rather than failing is the right trade — a forgotten preference costs one keystroke —
   * but it is a real regression and worth fixing if a stable id ever reappears.
   *
   * Returned raw so the caller is forced to hash it. It is a contact name or phone number.
   */
  private conversationIdFrom(main: Element): string | null {
    const title = resolve<HTMLElement>(SELECTORS.conversationTitle, main).element;
    const text = title?.textContent?.trim();
    return text ? `title:${text}` : null;
  }
}
