/**
 * Conversation identity probe.
 *
 * Paste into the DevTools console on the WhatsApp Web page - the page, not the service worker.
 *
 * WHY THIS EXISTS
 *
 * After thirty conversation switches between two chats, the Agent's store held fourteen distinct
 * conversation keys, several created seconds apart. The key is a salted hash of the chat title, so
 * fourteen keys means the title string this adapter reads is not stable: the same conversation is
 * being given a new identity on most visits, and any preference learned for it is orphaned
 * immediately.
 *
 * What is not yet known is WHAT varies. Presence text ("online", "last seen...", "typing...") is
 * the obvious suspect, but the header is also re-rendered constantly, and a different selector tier
 * may be matching on different visits. Guessing at a fix without knowing which would be the same
 * mistake that produced the September adapter breakage.
 *
 * WHAT IT PRINTS
 *
 * Nothing readable. Letters become x, digits become #, and spacing and punctuation are kept, so
 * "Dana Levi" and "Dana Levi typing..." are distinguishable as shapes while remaining unreadable.
 * That is enough to identify the variation and consistent with a product whose central claim is
 * that message content never leaves the machine. Read the masking below before running it.
 *
 * HOW TO USE
 *
 * 1. Open WhatsApp Web, F12, Console, on the page itself.
 * 2. Paste this whole file and press Enter.
 * 3. Switch between the same two conversations five or six times, typing a word in each.
 * 4. Type  autolangIdentityReport()  and send the table.
 */

(() => {
  const TIERS = [
    '[data-testid="conversation-info-header-chat-title"]',
    '#main header [data-testid="conversation-info-header"] span[dir="auto"]',
    '#main header span[dir="auto"]',
    '#main header',
  ];

  /** Letters to x, digits to #. Spacing, punctuation and length survive; nothing else does. */
  const mask = (text) =>
    text
      .replace(/\p{Lu}/gu, 'X')
      .replace(/\p{Ll}|\p{Lo}/gu, 'x')
      .replace(/\p{Nd}/gu, '#');

  const samples = [];
  let previous = null;

  function sample() {
    let tier = -1;
    let element = null;

    for (let i = 0; i < TIERS.length; i++) {
      const found = document.querySelector(TIERS[i]);
      if (found) {
        tier = i;
        element = found;
        break;
      }
    }

    if (!element) {
      if (previous !== null) {
        samples.push({ tier: -1, shape: '(no header)', length: 0, changed: true });
        previous = null;
      }
      return;
    }

    const raw = (element.textContent ?? '').trim();
    const shape = mask(raw);

    // Only transitions are interesting. Recording every tick would bury them.
    if (shape === previous) return;

    // How many candidate spans the header holds, and which one we took. If the count moves, the
    // header is being restructured and "the first span" is not a stable choice.
    const spans = document.querySelectorAll('#main header span[dir="auto"]').length;

    samples.push({
      at: new Date().toLocaleTimeString(),
      tier,
      spans,
      length: raw.length,
      shape,
      changed: previous !== null,
    });

    previous = shape;
  }

  const timer = setInterval(sample, 500);
  sample();

  window.autolangIdentityReport = () => {
    clearInterval(timer);

    const distinct = new Set(samples.map((s) => s.shape));

    console.log(
      `\n${samples.length} transitions, ${distinct.size} distinct title shapes.\n` +
        `Two conversations should produce two.\n`,
    );
    console.table(samples);

    console.log(
      'Shapes seen:\n' +
        [...distinct].map((s) => `  ${JSON.stringify(s)}`).join('\n') +
        '\n\nLetters are x, digits are #. No readable text was collected.',
    );

    return `${distinct.size} distinct shapes across ${samples.length} transitions`;
  };

  console.log(
    'AutoLang identity probe running.\n' +
      'Switch between your two conversations a few times, typing a word in each,\n' +
      'then run:  autolangIdentityReport()',
  );
})();
