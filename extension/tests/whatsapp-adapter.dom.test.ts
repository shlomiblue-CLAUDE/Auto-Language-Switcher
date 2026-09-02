import { beforeEach, describe, expect, it } from 'vitest';
import { WhatsAppAdapter } from '../src/adapters/whatsapp/adapter.js';
import { relevantLetters } from '../src/shared/text-normalizer.js';
import {
  ENGLISH_MESSAGE,
  HEBREW_MESSAGE,
  mountWhatsApp,
  type FixtureMessage,
} from './fixtures/whatsapp-dom.js';

const outgoing = (text: string): FixtureMessage => ({ text, outgoing: true });
const incoming = (text: string): FixtureMessage => ({ text, outgoing: false });

describe('WhatsAppAdapter', () => {
  let adapter: WhatsAppAdapter;

  beforeEach(() => {
    adapter = new WhatsAppAdapter();
    document.body.innerHTML = '';
  });

  describe('direction', () => {
    it('reads outgoing and incoming from data-id', () => {
      mountWhatsApp({ messages: [incoming(HEBREW_MESSAGE), outgoing(ENGLISH_MESSAGE)] });

      const { messages } = adapter.read(10);

      expect(messages).toHaveLength(2);
      expect(messages[0]!.direction).toBe('outgoing'); // newest first
      expect(messages[1]!.direction).toBe('incoming');
    });

    it('falls back to message-in and message-out classes when data-id is absent', () => {
      mountWhatsApp({
        messages: [incoming(HEBREW_MESSAGE), outgoing(ENGLISH_MESSAGE)],
        useLegacyClasses: true,
      });

      const { messages } = adapter.read(10);

      expect(messages.map((m) => m.direction)).toEqual(['outgoing', 'incoming']);
    });

    it('skips rows whose direction cannot be determined', () => {
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE)] });
      // A row with neither data-id nor a direction class: reading it would risk attributing the
      // other person's language to the user.
      const list = document.querySelector('div[role="application"]')!;
      list.insertAdjacentHTML('beforeend', '<div role="row"><span class="selectable-text"><span>hello</span></span></div>');

      const { messages } = adapter.read(10);

      expect(messages).toHaveLength(1);
      expect(messages[0]!.direction).toBe('outgoing');
    });
  });

  describe('ordering and limits', () => {
    it('returns newest first', () => {
      mountWhatsApp({ messages: [outgoing(HEBREW_MESSAGE), outgoing(ENGLISH_MESSAGE)] });

      const { messages } = adapter.read(10);

      expect(messages[0]!.counts.English).toBeGreaterThan(0);
      expect(messages[0]!.counts.Hebrew ?? 0).toBe(0);
      expect(messages[0]!.index).toBe(0);
      expect(messages[1]!.index).toBe(1);
    });

    it('keeps only the most recent messages', () => {
      const many = Array.from({ length: 25 }, () => outgoing(ENGLISH_MESSAGE));
      mountWhatsApp({ messages: many });

      expect(adapter.read(10).messages).toHaveLength(10);
    });

    it('drops messages that carry no letters', () => {
      mountWhatsApp({ messages: [outgoing('👍'), outgoing('12345'), outgoing(HEBREW_MESSAGE)] });

      const { messages } = adapter.read(10);

      expect(messages).toHaveLength(1);
      expect(messages[0]!.counts.Hebrew).toBeGreaterThan(0);
    });
  });

  describe('conversation identity', () => {
    it('prefers the chat id from data-id', () => {
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE)], chatId: '972500000000@c.us' });

      expect(adapter.read(10).rawConversationId).toBe('jid:972500000000@c.us');
    });

    it('falls back to the header title when no data-id is present', () => {
      mountWhatsApp({
        messages: [outgoing(ENGLISH_MESSAGE)],
        useLegacyClasses: true,
        headerTitle: 'Supplier Group',
      });

      expect(adapter.read(10).rawConversationId).toBe('title:Supplier Group');
    });

    it('is stable across reads of the same conversation', () => {
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE)] });
      const first = adapter.read(10).rawConversationId;

      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE), outgoing(HEBREW_MESSAGE)] });
      const second = adapter.read(10).rawConversationId;

      expect(second).toBe(first);
    });

    it('changes when the conversation changes', () => {
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE)], chatId: '972500000001@c.us' });
      const first = adapter.read(10).rawConversationId;

      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE)], chatId: '972500000002@c.us' });

      expect(adapter.read(10).rawConversationId).not.toBe(first);
    });
  });

  describe('composer, the typing guard input', () => {
    it('reports empty when the composer has no text', () => {
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE)] });

      expect(adapter.read(10).composerEmpty).toBe(true);
    });

    it('reports not empty while the user is typing', () => {
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE)], composerText: 'שלום' });

      expect(adapter.read(10).composerEmpty).toBe(false);
    });

    it('treats whitespace as empty', () => {
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE)], composerText: '   ' });

      expect(adapter.read(10).composerEmpty).toBe(true);
    });

    it('fails closed when the composer cannot be found', () => {
      // A missing composer means the page is not understood. Reporting "empty" would let a switch
      // through on no evidence, so the adapter reports "not empty" and suppresses switching.
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE)], omitComposer: true });

      expect(adapter.read(10).composerEmpty).toBe(false);
    });
  });

  describe('health', () => {
    it('is healthy on a well formed page', () => {
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE)] });

      const health = adapter.checkHealth();

      expect(health.healthy).toBe(true);
      expect(health.missing).toHaveLength(0);
    });

    it('reports missing required selectors when the layout is unrecognised', () => {
      document.body.innerHTML = '<div><p>something else entirely</p></div>';

      const health = adapter.checkHealth();

      expect(health.healthy).toBe(false);
      expect(health.missing).toContain('mainPanel');
      expect(health.missing).toContain('composer');
    });

    it('reports which fallback tier matched', () => {
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE)] });

      const { tiers } = adapter.checkHealth();

      // Tier 0 is the preferred selector. A rising number here is the early warning that
      // WhatsApp changed its DOM.
      expect(tiers.mainPanel).toBe(0);
      expect(tiers.composer).toBe(0);
    });

    it('returns an empty reading rather than throwing when no conversation is open', () => {
      mountWhatsApp({ messages: [], omitMain: true });

      const reading = adapter.read(10);

      expect(reading.rawConversationId).toBeNull();
      expect(reading.messages).toHaveLength(0);
      expect(reading.composerEmpty).toBe(true);
    });
  });

  describe('privacy', () => {
    it('produces nothing but counts, direction and index', () => {
      mountWhatsApp({
        messages: [outgoing('my bank pin is 4321 and my address is Herzl 5')],
        chatId: '972500000000@c.us',
        headerTitle: 'Private Contact',
      });

      const { messages } = adapter.read(10);
      const serialised = JSON.stringify(messages);

      expect(serialised).not.toContain('bank');
      expect(serialised).not.toContain('4321');
      expect(serialised).not.toContain('Herzl');
      expect(serialised).not.toContain('Private Contact');
      expect(serialised).not.toContain('972500000000');

      // Only the three permitted keys ever appear.
      for (const message of messages) {
        expect(Object.keys(message).sort()).toEqual(['counts', 'direction', 'index']);
      }
      expect(relevantLetters(messages[0]!.counts)).toBeGreaterThan(0);
    });

    it('returns the conversation id raw so the caller is forced to hash it', () => {
      // Documenting intent: the adapter must NOT hash. Hashing needs the install salt, which is
      // async storage the adapter has no business touching, and it is a privacy policy decision.
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE)], chatId: '972500000000@c.us' });

      expect(adapter.read(10).rawConversationId).toContain('972500000000');
    });
  });

  describe('observation root', () => {
    it('is the message list, not the document', () => {
      mountWhatsApp({ messages: [outgoing(ENGLISH_MESSAGE)] });

      const root = adapter.observationRoot();

      expect(root).not.toBeNull();
      expect(root).not.toBe(document.body);
      expect(root!.matches('#main div[role="application"]')).toBe(true);
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
