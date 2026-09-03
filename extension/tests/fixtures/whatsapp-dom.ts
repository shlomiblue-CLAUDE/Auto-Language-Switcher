import type { Measure, Rect } from '../../src/adapters/whatsapp/direction.js';

/**
 * WhatsApp Web DOM fixtures, rebuilt 3 September 2026 from a live session.
 *
 * The previous version of this file built the DOM WhatsApp had in 2024, and 49 tests passed
 * against it long after the real page had moved on. That is the specific failure worth naming: a
 * fixture is a copy of an assumption, and a green suite over a stale fixture is not evidence, it
 * is a stale assumption restated. These shapes came from measurement — see
 * docs/WHATSAPP_DOM_2026-09.md — and they carry a date for the same reason.
 *
 * What changed, and what the fixtures now reproduce:
 *   - no `message-in` / `message-out` classes anywhere
 *   - `data-id` is a bare message id, with no direction prefix and no chat JID
 *   - direction is expressed by which side of the panel a bubble sits on
 *   - `data-testid` carries the structure: conversation-panel-messages, msg-container, tail-in/out
 *
 * No real phone numbers, contact names or message text appear here.
 */

export interface FixtureMessage {
  readonly text: string;
  readonly outgoing: boolean;
  /** Suppresses the tail, as WhatsApp does for messages after the first in a group. */
  readonly grouped?: boolean;
  /** Renders a row with no bubble: a date divider or system notice. */
  readonly systemNotice?: boolean;
}

export interface FixtureOptions {
  readonly messages: readonly FixtureMessage[];
  readonly chatTitle?: string;
  /** Hebrew UI mirrors the layout, so the user's own messages move to the left. */
  readonly rtl?: boolean;
  readonly composerText?: string;
  readonly omitComposer?: boolean;
  readonly omitMain?: boolean;
  /** Drops every direction signal, reproducing the failure found against the live page. */
  readonly stripDirectionSignals?: boolean;
  /** Uses the pre-2026 shape, to prove the adapter no longer depends on it. */
  readonly legacyShape?: boolean;
}

/** Panel geometry the fake measure reports. Matches the live measurements closely enough. */
export const PANEL_LEFT = 0;
export const PANEL_RIGHT = 1300;
const BUBBLE_WIDTH = 200;
const EDGE_GAP = 65;

function bubbleRect(outgoing: boolean, rtl: boolean): Rect {
  // In RTL the user's own messages sit at the left edge; in LTR at the right.
  const ownSideIsLeft = rtl;
  const atLeft = outgoing === ownSideIsLeft;

  const left = atLeft ? PANEL_LEFT + EDGE_GAP : PANEL_RIGHT - EDGE_GAP - BUBBLE_WIDTH;
  return { left, right: left + BUBBLE_WIDTH, width: BUBBLE_WIDTH };
}

function messageHtml(message: FixtureMessage, index: number, options: FixtureOptions): string {
  if (message.systemNotice) {
    return `<div role="row"><div class="system-notice"><span>${message.text}</span></div></div>`;
  }

  const id = `3EB0${index.toString(16).toUpperCase().padStart(16, '0')}`;
  const rtl = options.rtl ?? false;

  if (options.legacyShape) {
    // The 2024 shape: direction in the data-id prefix and in a class. Neither exists any more.
    return `
      <div role="row" data-id="${message.outgoing}_972500000000@c.us_${id}">
        <div class="${message.outgoing ? 'message-out' : 'message-in'}">
          <div class="copyable-text"><span class="selectable-text"><span>${message.text}</span></span></div>
        </div>
      </div>`;
  }

  // Tails appear only on the first message of a group - and `tail-out` marks the OTHER person's
  // messages, which is the inversion recorded in direction.ts.
  const tail = message.grouped
    ? ''
    : `<span data-testid="${message.outgoing ? 'tail-in' : 'tail-out'}"></span>`;

  const senderLabel = options.stripDirectionSignals
    ? ''
    : `<span aria-label="${message.outgoing ? (rtl ? 'את/ה:' : 'You:') : 'Contact Name:'}"></span>`;

  const rect = bubbleRect(message.outgoing, rtl);

  return `
    <div role="row" data-id="${id}" data-testid="conv-msg-${id}">
      <div data-testid="msg-container" data-fixture-rect="${rect.left},${rect.right}">
        ${options.stripDirectionSignals ? '' : tail}
        ${senderLabel}
        <div class="copyable-text">
          <span class="selectable-text"><span>${message.text}</span></span>
        </div>
        <div data-testid="msg-meta"><span>12:34</span></div>
      </div>
    </div>`;
}

export function buildWhatsAppDom(options: FixtureOptions): string {
  if (options.omitMain) {
    return '<div id="app"><div class="landing-wrapper">No chat selected</div></div>';
  }

  const header = `
    <header data-testid="conversation-header">
      <div data-testid="conversation-info-header">
        <span data-testid="conversation-info-header-chat-title" dir="auto">${options.chatTitle ?? 'Test Conversation'}</span>
        <span data-testid="chat-subtitle">online</span>
      </div>
    </header>`;

  const composer = options.omitComposer
    ? ''
    : `<footer>
         <div data-testid="conversation-compose-box-input" contenteditable="true" role="textbox">${options.composerText ?? ''}</div>
       </footer>`;

  // Rendered oldest first, matching WhatsApp.
  const messages = options.messages.map((m, i) => messageHtml(m, i, options)).join('');

  return `
    <div id="app">
      <div id="main">
        ${header}
        <div data-testid="conversation-panel-messages" class="copyable-area">
          ${messages}
        </div>
        ${composer}
      </div>
    </div>`;
}

export function mountWhatsApp(options: FixtureOptions): void {
  document.body.innerHTML = buildWhatsAppDom(options);
}

/**
 * Stands in for layout, which jsdom does not perform - every real rect there is zero.
 *
 * Bubbles carry their intended rect in `data-fixture-rect`; anything else is the panel. This keeps
 * geometry genuinely exercised rather than skipped in tests and trusted in production.
 */
export const fixtureMeasure: Measure = (element) => {
  const encoded = element.getAttribute('data-fixture-rect');
  if (encoded) {
    const [left, right] = encoded.split(',').map(Number) as [number, number];
    return { left, right, width: right - left };
  }
  return { left: PANEL_LEFT, right: PANEL_RIGHT, width: PANEL_RIGHT - PANEL_LEFT };
};

export const HEBREW_MESSAGE = 'מה נשמע אחי הכל טוב';
export const ENGLISH_MESSAGE = 'hey there how are you doing';
