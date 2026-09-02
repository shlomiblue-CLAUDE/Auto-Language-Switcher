/**
 * Synthetic WhatsApp Web DOM.
 *
 * Honest label: this is built from the documented structure the selectors target, not scraped from
 * a live session. It proves the adapter's parsing, ordering, direction detection and fallback logic
 * are correct - it cannot prove the selectors match today's real WhatsApp. That check is a separate,
 * manual step (see tools/whatsapp-selector-probe.js) and must be repeated whenever WhatsApp ships a
 * redesign. Keeping the two kinds of verification separate is deliberate: a green suite here should
 * never be mistaken for "it works in production".
 *
 * No real phone numbers or message text appear anywhere in this file.
 */

export interface FixtureMessage {
  readonly text: string;
  readonly outgoing: boolean;
}

export interface FixtureOptions {
  readonly messages: readonly FixtureMessage[];
  readonly chatId?: string;
  readonly headerTitle?: string;
  /** Use message-in/message-out classes instead of data-id, exercising the fallback tier. */
  readonly useLegacyClasses?: boolean;
  readonly composerText?: string;
  readonly omitComposer?: boolean;
  readonly omitMain?: boolean;
}

const DEFAULT_CHAT_ID = '972500000000@c.us';

function messageHtml(message: FixtureMessage, index: number, options: FixtureOptions): string {
  const chatId = options.chatId ?? DEFAULT_CHAT_ID;
  const bubbleClass = message.outgoing ? 'message-out' : 'message-in';

  if (options.useLegacyClasses) {
    return `
      <div role="row">
        <div class="${bubbleClass}">
          <div class="copyable-text">
            <span class="selectable-text"><span>${message.text}</span></span>
          </div>
        </div>
      </div>`;
  }

  return `
    <div data-id="${message.outgoing}_${chatId}_3EB0${index.toString().padStart(4, '0')}" role="row">
      <div class="${bubbleClass}">
        <div class="copyable-text">
          <span class="selectable-text"><span>${message.text}</span></span>
        </div>
      </div>
    </div>`;
}

export function buildWhatsAppDom(options: FixtureOptions): string {
  if (options.omitMain) {
    return '<div id="app"><div class="landing-wrapper">No chat selected</div></div>';
  }

  const header = options.headerTitle
    ? `<header><div><span dir="auto">${options.headerTitle}</span></div></header>`
    : '<header></header>';

  const composer = options.omitComposer
    ? ''
    : `<footer>
         <div contenteditable="true" role="textbox" data-tab="10">${options.composerText ?? ''}</div>
       </footer>`;

  // Rendered oldest-first, matching WhatsApp.
  const messages = options.messages.map((m, i) => messageHtml(m, i, options)).join('');

  return `
    <div id="app">
      <div id="main">
        ${header}
        <div role="application" class="copyable-area">
          ${messages}
        </div>
        ${composer}
      </div>
    </div>`;
}

/** Installs a fixture into the jsdom document. */
export function mountWhatsApp(options: FixtureOptions): void {
  document.body.innerHTML = buildWhatsAppDom(options);
}

export const HEBREW_MESSAGE = 'מה נשמע אחי הכל טוב';
export const ENGLISH_MESSAGE = 'hey there how are you doing';
