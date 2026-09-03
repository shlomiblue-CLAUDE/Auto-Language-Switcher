import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ADAPTERS, ContentObserver, isContextInvalidated } from '../src/content/index.js';
import type { AdapterReading, SiteAdapter } from '../src/adapters/site-adapter.js';

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

  it('recognises the shape Chrome produces when it removes chrome.runtime outright', () => {
    // The one that got through, from a real screenshot: hundreds of identical
    // "read failed TypeError: Cannot read properties of undefined (reading 'sendMessage')" on a
    // WhatsApp tab. Chrome does not always leave a runtime behind that throws a named error -
    // sometimes the object is gone and the call fails as an ordinary TypeError that never mentions
    // the extension. Read only for the wording above, that looks like a transient fault, so the
    // observer kept running against a dead runtime and warned every 100ms until the tab was closed.
    expect(
      isContextInvalidated(new TypeError("Cannot read properties of undefined (reading 'sendMessage')")),
    ).toBe(true);
    expect(
      isContextInvalidated(new TypeError("Cannot read property 'sendMessage' of undefined")),
    ).toBe(true);
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
        // A live content script always has this. runtimeGone() reads it to notice the loss
        // before a call throws, so a stub without it looks orphaned from the first read.
        id: 'test-extension-id',
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
        // A live content script always has this. runtimeGone() reads it to notice the loss
        // before a call throws, so a stub without it looks orphaned from the first read.
        id: 'test-extension-id',
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

/**
 * The generic adapter made typing a first-class trigger, and typing fires an event per character.
 *
 * Every signal that carries a learned layout is a write to the Agent's conversation store, so an
 * unfiltered keystroke listener would rewrite it ten times a second and bury the decision log in
 * identical lines. These tests pin the shape of the filter: keystrokes are collapsed, and the
 * triggers that can change the Agent's answer without changing the page are not.
 */
describe('ContentObserver and the cost of typing', () => {
  let reading: AdapterReading;
  let observer: ContentObserver;

  const signals = () =>
    (chrome.runtime.sendMessage as ReturnType<typeof vi.fn>).mock.calls.filter(
      (call) => (call[0] as { type?: string })?.type === 'signal',
    ).length;

  function typingAdapter(): SiteAdapter {
    return {
      site: 'example.com',
      version: 'test',
      matches: () => true,
      // Null, exactly as GenericAdapter does: no MutationObserver, so events are the only trigger.
      observationRoot: () => null,
      read: () => reading,
      checkHealth: () => ({ healthy: true, missing: [], tiers: {} }),
    };
  }

  beforeEach(() => {
    vi.useFakeTimers();
    document.body.innerHTML = '<textarea id="box"></textarea>';
    reading = { rawConversationId: 'https://example.com|name=body', messages: [], composerEmpty: true };

    vi.stubGlobal('chrome', {
      runtime: {
        // A live content script always has this. runtimeGone() reads it to notice the loss
        // before a call throws, so a stub without it looks orphaned from the first read.
        id: 'test-extension-id',
        sendMessage: vi.fn(() => Promise.resolve({ stop: false })),
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
    observer?.stop();
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('sends once for a burst of keystrokes that changes nothing', async () => {
    observer = new ContentObserver(typingAdapter());
    observer.start();
    await vi.advanceTimersByTimeAsync(500);

    const before = signals();
    reading = { ...reading, composerEmpty: false };

    // The transition into typing is worth one signal: it is the moment the Agent learns which
    // layout the user is actually using here.
    document.dispatchEvent(new Event('input'));
    await vi.advanceTimersByTimeAsync(500);
    expect(signals()).toBe(before + 1);

    // The next forty characters tell it nothing it does not already know.
    for (let i = 0; i < 40; i++) {
      document.dispatchEvent(new Event('input'));
      await vi.advanceTimersByTimeAsync(150);
    }

    expect(signals()).toBe(before + 1);
  });

  it('sends again when the box empties, because a decision becomes possible', async () => {
    observer = new ContentObserver(typingAdapter());
    observer.start();
    await vi.advanceTimersByTimeAsync(500);

    reading = { ...reading, composerEmpty: false };
    document.dispatchEvent(new Event('input'));
    await vi.advanceTimersByTimeAsync(500);
    const whileTyping = signals();

    // The user sent the message and the box cleared.
    reading = { ...reading, composerEmpty: true };
    document.dispatchEvent(new Event('input'));
    await vi.advanceTimersByTimeAsync(500);

    expect(signals()).toBe(whileTyping + 1);
  });

  it('never filters a focus or a visibility change', async () => {
    observer = new ContentObserver(typingAdapter());
    observer.start();
    await vi.advanceTimersByTimeAsync(500);

    const before = signals();

    // Nothing about the page changed. The Agent's answer still can: it reads the foreground window
    // and the live layout itself, and neither is visible from this page. Returning to a tab is
    // precisely when the user is about to type.
    document.dispatchEvent(new Event('focusin'));
    await vi.advanceTimersByTimeAsync(500);

    expect(signals()).toBe(before + 1);
  });

  it('stops for good when the service worker says the site is no longer allowed', async () => {
    (chrome.runtime.sendMessage as ReturnType<typeof vi.fn>).mockResolvedValue({ stop: true });

    observer = new ContentObserver(typingAdapter());
    observer.start();
    await vi.advanceTimersByTimeAsync(500);

    const afterRevoke = signals();

    // Chrome leaves an injected content script running after its permission is withdrawn, so this
    // reply is the only way the page finds out. Every trigger must be dead, not just the timer.
    document.dispatchEvent(new Event('focusin'));
    document.dispatchEvent(new Event('input'));
    document.querySelector('#box')!.appendChild(document.createElement('span'));
    await vi.advanceTimersByTimeAsync(30_000);

    expect(signals()).toBe(afterRevoke);
  });
});

describe('adapter selection', () => {
  it('prefers a site with an adapter of its own, and falls back to the generic one', () => {
    const whatsapp = ADAPTERS.find((a) => a.matches({ hostname: 'web.whatsapp.com' } as Location));
    const gmail = ADAPTERS.find((a) => a.matches({ hostname: 'mail.google.com' } as Location));

    expect(whatsapp?.site).toBe('web.whatsapp.com');
    // Reading a real conversation beats anything that can be inferred from a text box, so order
    // is the whole mechanism here: GenericAdapter matches everything and must come last.
    expect(gmail).toBe(ADAPTERS[ADAPTERS.length - 1]);
  });
});
