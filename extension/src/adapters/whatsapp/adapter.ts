import type {
  AdapterHealth,
  AdapterReading,
  ObservedMessage,
  SiteAdapter,
} from '../site-adapter.js';
import { analyze, relevantLetters } from '../../shared/text-normalizer.js';
import {
  SELECTORS,
  chatIdFromRow,
  directionOf,
  resolve,
  resolveAll,
  type SelectorSpec,
} from './selectors.js';

/**
 * Reads WhatsApp Web and produces letter counts.
 *
 * Two constraints shape everything here.
 *
 * Privacy: text is read, counted and discarded inside this function. Nothing that leaves carries
 * a character the user wrote. The only PII that even briefly exists is the chat JID, returned raw
 * purely so the caller can hash it.
 *
 * Fragility: WhatsApp is a single-page app whose DOM changes without notice. The adapter therefore
 * reports how well it recognised the page (`checkHealth`) rather than assuming it succeeded, so
 * the product can say "layout not recognized" instead of switching on garbage.
 */
export class WhatsAppAdapter implements SiteAdapter {
  readonly site = 'web.whatsapp.com';

  /** Bump on any selector change. Surfaced in debug output so a field report identifies the build. */
  readonly version = '1.0.0';

  matches(location: Location): boolean {
    return location.hostname === 'web.whatsapp.com';
  }

  observationRoot(): Element | null {
    // Observing the message list, not document. PDR section 8 forbids scanning the whole document
    // on every mutation, and WhatsApp mutates constantly for reasons unrelated to messages.
    return resolve(SELECTORS.messageList).element ?? resolve(SELECTORS.mainPanel).element;
  }

  read(maxMessages: number): AdapterReading {
    const main = resolve(SELECTORS.mainPanel).element;
    if (!main) {
      // No conversation open. Not an error - this is the landing screen.
      return { rawConversationId: null, messages: [], composerEmpty: true };
    }

    const { elements: rows } = resolveAll(SELECTORS.messageRow, main);

    // Rendered order is oldest to newest, so the tail is the recent history we care about.
    const recent = rows.slice(-maxMessages).reverse();

    const messages: ObservedMessage[] = [];
    for (let index = 0; index < recent.length; index++) {
      const row = recent[index]!;
      const direction = directionOf(row);
      if (!direction) continue; // Unknown direction is worse than no data. Skip it.

      const counts = analyze(this.textOf(row));
      if (relevantLetters(counts) === 0) continue;

      messages.push({ direction, counts, index });
    }

    return {
      rawConversationId: this.conversationIdFrom(rows, main),
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

    return { healthy: missing.length === 0, missing, tiers };
  }

  /**
   * Text of one bubble.
   *
   * Falls back to the row's own textContent when no text span matches, which picks up timestamps
   * and status words. That is acceptable: those are digits and short strings that the letter
   * counter discards anyway, and over-reading is safer than silently reading nothing.
   */
  private textOf(row: Element): string {
    const { elements } = resolveAll(SELECTORS.messageText, row);
    if (elements.length === 0) return row.textContent ?? '';

    return elements.map((element) => element.textContent ?? '').join(' ');
  }

  /**
   * Conversation identity, most stable source first.
   *
   * The JID from data-id is preferred because it survives renames and redesigns. The header title
   * is a weaker fallback: it changes when a contact is renamed, which silently orphans a saved
   * preference. Both are PII and hashed by the caller.
   */
  private conversationIdFrom(rows: readonly Element[], main: Element): string | null {
    for (const row of rows) {
      const jid = chatIdFromRow(row);
      if (jid) return `jid:${jid}`;
    }

    const header = resolve<HTMLElement>(SELECTORS.conversationHeader, main).element;
    const title = header?.textContent?.trim();
    return title ? `title:${title}` : null;
  }
}
