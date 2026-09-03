import { beforeEach, describe, expect, it, vi } from 'vitest';
import { WhatsAppAdapter } from '../src/adapters/whatsapp/adapter.js';
import { relevantLetters } from '../src/shared/text-normalizer.js';
import {
  ENGLISH_MESSAGE,
  HEBREW_MESSAGE,
  fixtureMeasure,
  mountWhatsApp,
  type FixtureMessage,
} from './fixtures/whatsapp-dom.js';

const outgoing = (text: string, extra: Partial<FixtureMessage> = {}): FixtureMessage => ({
  text,
  outgoing: true,
  ...extra,
});
const incoming = (text: string, extra: Partial<FixtureMessage> = {}): FixtureMessage => ({
  text,
  outgoing: false,
  ...extra,
});

/**
 * jsdom does not lay anything out, so `getComputedStyle(panel).direction` is always the initial
 * value. Direction is a property of the page under test, so it is stubbed per test rather than
 * inferred.
 */
function withPanelDirection(direction: 'ltr' | 'rtl'): void {
  vi.spyOn(window, 'getComputedStyle').mockImplementation(
    () => ({ direction }) as unknown as CSSStyleDeclaration,
  );
}

describe('WhatsAppAdapter', () => {
  let adapter: WhatsAppAdapter;

  beforeEach(() => {
    vi.restoreAllMocks();
    adapter = new WhatsAppAdapter(fixtureMeasure);
    document.body.innerHTML = '';
  });

  describe('direction, by geometry', () => {
    it('reads a left-to-right layout: the user is on the right', () => {
      withPanelDirection('ltr');
      mountWhatsApp({ messages: [incoming(HEBREW_MESSAGE), outgoing(ENGLISH_MESSAGE)] });

      const { messages } = adapter.read(10);

      expect(messages).toHaveLength(2);
      expect(messages[0]!.direction).toBe('outgoing'); // newest first
      expect(messages[1]!.direction).toBe('incoming');
    });

    it('reads a right-to-left layout, where the sides are mirrored', () => {
      // The trap: in a Hebrew UI the user's own messages move to the LEFT. Reading the side
      // without consulting the panel's direction inverts every decision the product makes.
      withPanelDirection('rtl');
      mountWhatsApp({ messages: [incoming(HEBREW_MESSAGE), outgoing(ENGLISH_MESSAGE)], rtl: true });

      const { messages } = adapter.read(10);

      expect(messages[0]!.direction).toBe('outgoing');
      expect(messages[1]!.direction).toBe('incoming');
    });

    it('declines to call a bubble that spans most of the panel', () => {
      withPanelDirection('ltr');
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE)] });

      // A long message leaves both gaps small and similar, which is a coin toss, so geometry
      // abstains and the sender label decides instead.
      const bubble = document.querySelector('[data-testid="msg-container"]')!;
      bubble.setAttribute('data-fixture-rect', '20,1280');

      const { messages } = adapter.read(10);

      expect(messages[0]!.direction).toBe('outgoing'); // via the sender label
    });
  });

  describe('direction, by fallback', () => {
    it('falls back to the sender label when geometry cannot decide', () => {
      withPanelDirection('ltr');
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE), incoming(HEBREW_MESSAGE)] });

      for (const bubble of document.querySelectorAll('[data-testid="msg-container"]')) {
        bubble.setAttribute('data-fixture-rect', '20,1280');
      }

      const { messages } = adapter.read(10);

      expect(messages.map((m) => m.direction)).toEqual(['incoming', 'outgoing']);
    });

    it('recognises the Hebrew self marker', () => {
      withPanelDirection('rtl');
      mountWhatsApp({ messages: [outgoing(HEBREW_MESSAGE)], rtl: true });
      document.querySelector('[data-testid="msg-container"]')!.setAttribute('data-fixture-rect', '20,1280');

      expect(adapter.read(10).messages[0]!.direction).toBe('outgoing');
    });

    it('falls back to the tail, which means the opposite of its name', () => {
      // tail-out marks the OTHER person's messages. Measured, not assumed.
      withPanelDirection('ltr');
      mountWhatsApp({ messages: [incoming(HEBREW_MESSAGE)], stripDirectionSignals: false });

      const row = document.querySelector('[role="row"]')!;
      row.querySelector('[aria-label]')?.remove();
      row.querySelector('[data-testid="msg-container"]')!.setAttribute('data-fixture-rect', '20,1280');

      expect(row.querySelector('[data-testid="tail-out"]')).not.toBeNull();
      expect(adapter.read(10).messages[0]!.direction).toBe('incoming');
    });

    it('skips a row when every signal is gone', () => {
      // This is what the live page did to the previous adapter. Dropping the row is correct:
      // a guessed direction feeds the other person's language into the user's keyboard.
      withPanelDirection('ltr');
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE)], stripDirectionSignals: true });
      document.querySelector('[data-testid="msg-container"]')!.setAttribute('data-fixture-rect', '20,1280');

      expect(adapter.read(10).messages).toHaveLength(0);
    });
  });

  describe('the DOM that broke', () => {
    it('no longer depends on data-id carrying a direction prefix', () => {
      withPanelDirection('ltr');
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE), incoming(HEBREW_MESSAGE)] });

      const ids = [...document.querySelectorAll('[role="row"][data-id]')].map((r) =>
        r.getAttribute('data-id'),
      );

      expect(ids.every((id) => !id!.includes('_'))).toBe(true);
      expect(adapter.read(10).messages).toHaveLength(2);
    });

    it('no longer depends on message-in and message-out classes', () => {
      withPanelDirection('ltr');
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE)] });

      expect(document.querySelectorAll('.message-out, .message-in')).toHaveLength(0);
      expect(adapter.read(10).messages[0]!.direction).toBe('outgoing');
    });

    it('still reads the pre-2026 shape through the sender label', () => {
      // Not a requirement, but worth knowing: the old markup is not actively rejected.
      withPanelDirection('ltr');
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE)], legacyShape: true });

      // The legacy fixture has no msg-container, so it is skipped as a non-message row.
      expect(adapter.read(10).messages).toHaveLength(0);
    });
  });

  describe('rows that are not messages', () => {
    it('skips date dividers and system notices', () => {
      withPanelDirection('ltr');
      mountWhatsApp({
        messages: [
          { text: 'TODAY', outgoing: false, systemNotice: true },
          outgoing(ENGLISH_MESSAGE),
        ],
      });

      const { messages } = adapter.read(10);

      expect(messages).toHaveLength(1);
      expect(messages[0]!.direction).toBe('outgoing');
    });
  });

  describe('ordering and limits', () => {
    it('returns newest first with contiguous indices', () => {
      withPanelDirection('ltr');
      mountWhatsApp({ messages: [outgoing(HEBREW_MESSAGE), outgoing(ENGLISH_MESSAGE)] });

      const { messages } = adapter.read(10);

      expect(messages[0]!.counts.English).toBeGreaterThan(0);
      expect(messages.map((m) => m.index)).toEqual([0, 1]);
    });

    it('keeps only the most recent messages', () => {
      withPanelDirection('ltr');
      mountWhatsApp({ messages: Array.from({ length: 25 }, () => outgoing(ENGLISH_MESSAGE)) });

      expect(adapter.read(10).messages).toHaveLength(10);
    });

    it('drops messages that carry no letters, without leaving a gap in the indices', () => {
      withPanelDirection('ltr');
      mountWhatsApp({
        messages: [outgoing('👍'), outgoing('12345'), outgoing(HEBREW_MESSAGE)],
      });

      const { messages } = adapter.read(10);

      expect(messages).toHaveLength(1);
      expect(messages[0]!.index).toBe(0);
    });
  });

  describe('conversation identity', () => {
    it('comes from the header title', () => {
      withPanelDirection('ltr');
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE)], chatTitle: 'Supplier Group' });

      expect(adapter.read(10).rawConversationId).toBe('title:Supplier Group');
    });

    it('changes when the conversation changes', () => {
      withPanelDirection('ltr');
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE)], chatTitle: 'Chat A' });
      const first = adapter.read(10).rawConversationId;

      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE)], chatTitle: 'Chat B' });

      expect(adapter.read(10).rawConversationId).not.toBe(first);
    });

    it('is returned raw so the caller is forced to hash it', () => {
      // Documented intent: the adapter must NOT hash. Hashing needs the install salt, which is
      // async storage the adapter has no business touching, and it is a policy decision.
      withPanelDirection('ltr');
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE)], chatTitle: '+972 50 000 0000' });

      expect(adapter.read(10).rawConversationId).toContain('972');
    });
  });

  describe('composer, the typing guard input', () => {
    it('reports empty when the composer has no text', () => {
      withPanelDirection('ltr');
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE)] });

      expect(adapter.read(10).composerEmpty).toBe(true);
    });

    it('reports not empty while the user is typing', () => {
      withPanelDirection('ltr');
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE)], composerText: 'שלום' });

      expect(adapter.read(10).composerEmpty).toBe(false);
    });

    it('fails closed when the composer cannot be found', () => {
      withPanelDirection('ltr');
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE)], omitComposer: true });

      expect(adapter.read(10).composerEmpty).toBe(false);
    });
  });

  describe('health', () => {
    it('is healthy on a page it understands', () => {
      withPanelDirection('ltr');
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE), incoming(HEBREW_MESSAGE)] });

      const health = adapter.checkHealth();

      expect(health.healthy).toBe(true);
      expect(health.missing).toHaveLength(0);
    });

    it('reports unhealthy when there are messages it cannot read the direction of', () => {
      // The check that would have caught the September breakage. The previous adapter matched
      // every structural selector and still understood nothing, and reported itself healthy.
      withPanelDirection('ltr');
      mountWhatsApp({
        messages: [outgoing(ENGLISH_MESSAGE), incoming(HEBREW_MESSAGE)],
        stripDirectionSignals: true,
      });

      for (const bubble of document.querySelectorAll('[data-testid="msg-container"]')) {
        bubble.setAttribute('data-fixture-rect', '20,1280');
      }

      const health = adapter.checkHealth();

      expect(health.healthy).toBe(false);
      expect(health.missing).toContain('messageDirection');
    });

    it('is healthy on the landing screen, where no conversation is open', () => {
      // The chat list is present and the conversation panel is not. Reporting the layout as
      // unrecognised here blames WhatsApp for the user simply not having picked a chat.
      document.body.innerHTML = '<div id="pane-side"><div role="grid"><div role="row"></div></div></div>';

      const health = adapter.checkHealth();

      expect(health.healthy).toBe(true);
      expect(health.missing).toHaveLength(0);
    });

    it('reports missing required selectors when the layout is unrecognised', () => {
      // Neither a conversation panel nor a chat list: something really has changed.
      document.body.innerHTML = '<div><p>something else entirely</p></div>';

      const health = adapter.checkHealth();

      expect(health.healthy).toBe(false);
      expect(health.missing).toContain('mainPanel');
      expect(health.missing).toContain('composer');
    });

    it('reports which tier matched, as an early warning of drift', () => {
      withPanelDirection('ltr');
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE)] });

      const { tiers } = adapter.checkHealth();

      expect(tiers.mainPanel).toBe(0);
      expect(tiers.messagesPanel).toBe(0);
      expect(tiers.composer).toBe(0);
    });

    it('reports how direction was determined', () => {
      withPanelDirection('ltr');
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE), incoming(HEBREW_MESSAGE)] });

      const evidence = adapter.directionEvidence();

      expect(evidence.rowsWithBubbles).toBe(2);
      expect(evidence.resolved).toBe(2);
      expect(evidence.by.geometry).toBe(2);
    });

    it('returns an empty reading rather than throwing when no conversation is open', () => {
      mountWhatsApp({ messages: [], omitMain: true });

      const reading = adapter.read(10);

      expect(reading.rawConversationId).toBeNull();
      expect(reading.messages).toHaveLength(0);
    });
  });

  describe('privacy', () => {
    it('produces nothing but counts, direction and index', () => {
      withPanelDirection('ltr');
      mountWhatsApp({
        messages: [outgoing('my bank pin is 4321 and my address is Herzl 5')],
        chatTitle: 'Private Contact',
      });

      const { messages } = adapter.read(10);
      const serialised = JSON.stringify(messages);

      expect(serialised).not.toContain('bank');
      expect(serialised).not.toContain('4321');
      expect(serialised).not.toContain('Herzl');
      expect(serialised).not.toContain('Private Contact');

      for (const message of messages) {
        expect(Object.keys(message).sort()).toEqual(['counts', 'direction', 'index']);
      }
      expect(relevantLetters(messages[0]!.counts)).toBeGreaterThan(0);
    });
  });

  describe('observation root', () => {
    it('is the message panel, not the document', () => {
      withPanelDirection('ltr');
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE)] });

      const root = adapter.observationRoot();

      expect(root).not.toBeNull();
      expect(root!.getAttribute('data-testid')).toBe('conversation-panel-messages');
    });
  });

  describe('site matching', () => {
    it('claims only web.whatsapp.com', () => {
      expect(adapter.matches({ hostname: 'web.whatsapp.com' } as Location)).toBe(true);
      expect(adapter.matches({ hostname: 'whatsapp.com' } as Location)).toBe(false);
      expect(adapter.matches({ hostname: 'evil-web.whatsapp.com.attacker.test' } as Location)).toBe(false);
    });
  });
});
