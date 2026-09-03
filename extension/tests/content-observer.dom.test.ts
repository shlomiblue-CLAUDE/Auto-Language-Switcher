import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ContentObserver, isContextInvalidated } from '../src/content/index.js';
import type { SiteAdapter } from '../src/adapters/site-adapter.js';

/**
 * Covers the failure a user hit in the field: reloading the extension leaves the old content
 * script running in every open page with its `chrome.*` connection severed. Every call then throws
 * and there is no way back.
 *
 * The bug was not the error — that is normal, and happens on every extension update. The bug was
 * that the observer kept firing every 100ms against a dead runtime, throwing an unhandled
 * rejection each time, forever.
 */

const INVALIDATED = () => new Error('Extension context invalidated.');

function fakeAdapter(): SiteAdapter {
  return {
    site: 'web.whatsapp.com',
    version: 'test',
    matches: () => true,
    observationRoot: () => document.body,
    read: () => ({ rawConversationId: 'title:Test', messages: [], composerEmpty: true }),
    checkHealth: () => ({ healthy: true, missing: [], tiers: {} }),
  };
}

describe('isContextInvalidated', () => {
  it('recognises the message Chrome actually produces', () => {
    expect(isContextInvalidated(new Error('Extension context invalidated.'))).toBe(true);
    expect(isContextInvalidated(new Error('Extension context was invalidated.'))).toBe(true);
    expect(isContextInvalidated('Extension context invalidated')).toBe(true);
  });

  it('does not swallow unrelated failures', () => {
    // Treating every error as fatal would silently stop the product on a transient fault.
    expect(isContextInvalidated(new Error('Could not establish connection'))).toBe(false);
    expect(isContextInvalidated(new Error('The message port closed'))).toBe(false);
    expect(isContextInvalidated(undefined)).toBe(false);
  });
});

describe('ContentObserver when the extension is reloaded', () => {
  let observer: ContentObserver;

  beforeEach(() => {
    vi.useFakeTimers();
    document.body.innerHTML = '<div id="main"></div>';

    vi.stubGlobal('chrome', {
      runtime: {
        sendMessage: vi.fn(() => Promise.reject(INVALIDATED())),
        onMessage: { addListener: vi.fn() },
      },
      storage: { local: { get: vi.fn(async () => ({})), set: vi.fn(async () => {}) } },
    });

    vi.spyOn(console, 'info').mockImplementation(() => {});
    vi.spyOn(console, 'warn').mockImplementation(() => {});
  });

  afterEach(() => {
    observer?.stop();
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('stops scheduling once the context is gone', async () => {
    observer = new ContentObserver(fakeAdapter());
    observer.start();

    // Long enough for the first read to finish completely. A shorter wait captured the count
    // mid-read, between the health send and the signal send, and the second one then looked like
    // the observer had carried on.
    await vi.advanceTimersByTimeAsync(2_000);
    const callsAfterFirst = (chrome.runtime.sendMessage as ReturnType<typeof vi.fn>).mock.calls.length;
    expect(callsAfterFirst).toBeGreaterThan(0);

    // Thirty seconds of the page mutating as WhatsApp always does.
    await vi.advanceTimersByTimeAsync(30_000);

    expect((chrome.runtime.sendMessage as ReturnType<typeof vi.fn>).mock.calls.length).toBe(callsAfterFirst);
  });

  it('says what to do about it, once', async () => {
    observer = new ContentObserver(fakeAdapter());
    observer.start();

    await vi.advanceTimersByTimeAsync(200);
    await vi.advanceTimersByTimeAsync(30_000);

    const notices = (console.info as ReturnType<typeof vi.fn>).mock.calls.filter((call) =>
      String(call[0]).includes('reload this tab'),
    );

    expect(notices).toHaveLength(1);
  });

  it('survives a storage call throwing rather than rejecting', async () => {
    // chrome.storage throws synchronously in some Chrome versions when the context is gone, which
    // is what produced the unhandled rejection rather than a caught one.
    (chrome.storage.local.get as ReturnType<typeof vi.fn>).mockImplementation(() => {
      throw INVALIDATED();
    });

    observer = new ContentObserver(fakeAdapter());
    observer.start();

    await expect(vi.advanceTimersByTimeAsync(30_000)).resolves.not.toThrow();
  });
});

describe('ContentObserver in normal operation', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    document.body.innerHTML = '<div id="main"></div>';
    vi.stubGlobal('chrome', {
      runtime: {
        sendMessage: vi.fn(() => Promise.resolve()),
        onMessage: { addListener: vi.fn() },
      },
      storage: {
        local: { get: vi.fn(async () => ({ installSalt: 'a'.repeat(64) })), set: vi.fn(async () => {}) },
      },
    });
    vi.stubGlobal('crypto', {
      getRandomValues: (a: Uint8Array) => a,
      subtle: { digest: async () => new Uint8Array(32).buffer },
    });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('keeps observing when a send merely fails transiently', async () => {
    (chrome.runtime.sendMessage as ReturnType<typeof vi.fn>).mockRejectedValue(
      new Error('Could not establish connection. Receiving end does not exist.'),
    );
    vi.spyOn(console, 'warn').mockImplementation(() => {});

    const observer = new ContentObserver(fakeAdapter());
    observer.start();

    await vi.advanceTimersByTimeAsync(200);
    const first = (chrome.runtime.sendMessage as ReturnType<typeof vi.fn>).mock.calls.length;

    // A sleeping service worker comes back; the observer must still be running when it does.
    document.querySelector('#main')!.appendChild(document.createElement('div'));
    await vi.advanceTimersByTimeAsync(5_000);

    expect((chrome.runtime.sendMessage as ReturnType<typeof vi.fn>).mock.calls.length).toBeGreaterThan(first);
    observer.stop();
  });
});
