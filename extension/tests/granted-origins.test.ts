import { beforeAll, describe, expect, it, vi } from 'vitest';

/**
 * The match test that decides whether a page's signal is forwarded or refused.
 *
 * It used to be a formality — one declared host, and Chrome enforcing it anyway. It stopped being
 * one when sites became grantable individually: this is now the check standing between a site the
 * user has withdrawn and the conversation store, and it is the reason a wrong answer here is
 * silent in both directions. Too strict and the product does nothing, with no error anywhere. Too
 * loose and "stop using this site" does not.
 */

let originMatches: (pattern: string, url: string) => boolean;

beforeAll(async () => {
  // The service worker registers its listeners at import time, so it needs a chrome to attach to
  // before the module body runs.
  const noop = { addListener: () => {} };
  vi.stubGlobal('chrome', {
    runtime: { onStartup: noop, onInstalled: noop, onMessage: noop },
    permissions: { getAll: async () => ({ origins: [] }), onAdded: noop, onRemoved: noop },
    scripting: { getRegisteredContentScripts: async () => [] },
    action: { setBadgeText: () => {}, setBadgeBackgroundColor: () => {}, setTitle: () => {} },
    tabs: { query: async () => [] },
  });

  ({ originMatches } = await import('../src/background/service-worker.js'));
});

describe('originMatches', () => {
  it('lets the all-hosts pattern cover everything', () => {
    // This is what "Use on all sites" grants, and every signal from every page is then checked
    // against this one pattern.
    for (const url of [
      'https://mail.google.com/mail/u/0',
      'http://localhost:3000/',
      'https://web.whatsapp.com/',
      'https://example.co.il/a/b?c=d',
    ]) {
      expect(originMatches('*://*/*', url)).toBe(true);
    }
  });

  it('matches a granted host and nothing beside it', () => {
    expect(originMatches('https://mail.google.com/*', 'https://mail.google.com/mail/u/0')).toBe(true);

    // A different subdomain of the same company is a different site, and was never granted.
    expect(originMatches('https://mail.google.com/*', 'https://docs.google.com/')).toBe(false);
    expect(originMatches('https://mail.google.com/*', 'https://google.com/')).toBe(false);

    // Nor is a host that merely ends with the granted one.
    expect(originMatches('https://google.com/*', 'https://notgoogle.com/')).toBe(false);
  });

  it('honours a subdomain wildcard, including the bare domain', () => {
    expect(originMatches('https://*.google.com/*', 'https://mail.google.com/')).toBe(true);
    expect(originMatches('https://*.google.com/*', 'https://google.com/')).toBe(true);
    expect(originMatches('https://*.google.com/*', 'https://google.com.evil.test/')).toBe(false);
  });

  it('does not let http stand in for https', () => {
    expect(originMatches('https://example.com/*', 'http://example.com/')).toBe(false);
    expect(originMatches('*://example.com/*', 'http://example.com/')).toBe(true);
  });

  it('matches an origin with no trailing slash, which is what a sender reports', () => {
    // chrome.runtime.MessageSender.origin has no path. If this failed, every signal from every
    // granted site would be refused - and the only symptom would be the product doing nothing.
    expect(originMatches('https://example.com/*', 'https://example.com')).toBe(true);
  });

  it('fails closed on anything it cannot parse', () => {
    expect(originMatches('not-a-pattern', 'https://example.com/')).toBe(false);
    expect(originMatches('https://example.com/*', 'not-a-url')).toBe(false);
    expect(originMatches('', '')).toBe(false);
  });
});
