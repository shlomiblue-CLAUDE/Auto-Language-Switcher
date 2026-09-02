import type { OutboundMessage } from '../shared/protocol.js';

/**
 * Transport only. No decisions are made here.
 *
 * PDR section 7 places the decision engine in this service worker; the plan moves it into the
 * Agent instead, for two reasons. MV3 service workers are evicted aggressively, and PDR section 18
 * lists "Service Worker נרדם" as a live risk - a process that stays alive does not have it. And a
 * desktop signal source added later must reach the same engine and the same per-conversation
 * memory; keeping one brain in one process is what makes that possible without a second
 * implementation in another language.
 *
 * Phase 4 replaces the logging below with the native messaging port.
 */

const DEBUG = false;

chrome.runtime.onMessage.addListener((message: OutboundMessage, sender) => {
  if (!message || typeof message !== 'object') return false;

  switch (message.type) {
    case 'signal':
      if (DEBUG) {
        // Never log counts alongside anything identifying; this is a shape check only.
        console.debug('[autolang] signal', {
          site: message.site,
          messages: message.messages.length,
          composerEmpty: message.composerEmpty,
          tab: sender.tab?.id,
        });
      }
      // Phase 4: forward to the Agent over native messaging.
      break;

    case 'health':
      if (!message.healthy) {
        console.warn('[autolang] adapter health degraded', {
          site: message.site,
          adapterVersion: message.adapterVersion,
          missing: message.missing,
        });
      }
      break;
  }

  return false;
});
