/**
 * WhatsApp selector probe.
 *
 * Paste into the DevTools console on an open WhatsApp Web conversation. It reports which selector
 * tier the adapter would match, so real-DOM drift is caught before it reaches users.
 *
 * The output is safe to share. This script reads message text only to count letters by script, and
 * counts are all it ever prints - no message content, no contact names, and phone numbers reduced
 * to their shape (digit runs become "#"). Read the code before running it; that is the point of
 * shipping it as readable source rather than a bundled blob.
 *
 * Usage:
 *   1. Open https://web.whatsapp.com and select a conversation.
 *   2. F12 -> Console. Chrome may require you to type "allow pasting" first.
 *   3. Paste this whole file and press Enter.
 *   4. Copy the printed JSON.
 */

(() => {
  const SELECTORS = {
    mainPanel: {
      required: true,
      tiers: ['#main', '[data-testid="conversation-panel-wrapper"]', 'div[role="application"] > div:last-child'],
    },
    conversationHeader: {
      required: false,
      tiers: ['#main header', 'header[data-testid="conversation-header"]', '#main > header'],
    },
    messageList: {
      required: true,
      tiers: [
        '#main div[role="application"]',
        '#main div.copyable-area',
        '#main [data-testid="conversation-panel-messages"]',
        '#main',
      ],
    },
    messageRow: {
      required: false,
      tiers: ['div[data-id]', 'div[role="row"]', 'div.message-in, div.message-out'],
    },
    messageText: {
      required: false,
      tiers: ['span.selectable-text span', 'span.selectable-text', '.copyable-text span[dir]', '[dir="auto"]'],
    },
    composer: {
      required: true,
      tiers: [
        '#main footer div[contenteditable="true"]',
        '#main footer [role="textbox"]',
        'footer div[contenteditable="true"][data-tab]',
        'div[contenteditable="true"][role="textbox"]',
      ],
    },
  };

  const resolve = (spec, root = document) => {
    for (let tier = 0; tier < spec.tiers.length; tier++) {
      const element = root.querySelector(spec.tiers[tier]);
      if (element) return { element, tier, selector: spec.tiers[tier] };
    }
    return { element: null, tier: -1, selector: null };
  };

  const LETTER = /\p{L}/u;
  const inRange = (cp, start, end) => cp >= start && cp <= end;

  const countLetters = (text) => {
    let hebrew = 0;
    let latin = 0;
    for (const char of text ?? '') {
      const cp = char.codePointAt(0);
      if (!LETTER.test(char)) continue;
      if (inRange(cp, 0x0590, 0x05ff) || inRange(cp, 0xfb1d, 0xfb4f)) hebrew++;
      else if (inRange(cp, 0x41, 0x5a) || inRange(cp, 0x61, 0x7a)) latin++;
    }
    return { hebrew, latin };
  };

  /** Keeps the structure of a data-id visible while destroying its content. */
  const redact = (value) =>
    value == null ? null : value.replace(/\d/g, '#').replace(/[A-Za-z0-9]{8,}/g, '<id>');

  const report = { probeVersion: 1, adapterVersion: '1.0.0', url: location.origin, selectors: {} };

  for (const [name, spec] of Object.entries(SELECTORS)) {
    const { tier, selector } = resolve(spec);
    report.selectors[name] = {
      matchedTier: tier,
      matchedSelector: selector,
      status: tier === 0 ? 'preferred' : tier > 0 ? 'FALLBACK' : spec.required ? 'MISSING (required)' : 'missing',
    };
  }

  const main = resolve(SELECTORS.mainPanel).element;

  if (main) {
    const rowSpec = SELECTORS.messageRow;
    let rows = [];
    let rowTier = -1;
    for (let tier = 0; tier < rowSpec.tiers.length; tier++) {
      const found = Array.from(main.querySelectorAll(rowSpec.tiers[tier]));
      if (found.length) {
        rows = found;
        rowTier = tier;
        break;
      }
    }

    const sample = rows.slice(-10);
    let outgoing = 0;
    let incoming = 0;
    let unknownDirection = 0;
    let withText = 0;
    let hebrew = 0;
    let latin = 0;

    for (const row of sample) {
      const dataId = row.getAttribute('data-id');
      const direction =
        dataId?.startsWith('true_') ? 'outgoing'
        : dataId?.startsWith('false_') ? 'incoming'
        : row.classList.contains('message-out') || row.querySelector('.message-out') ? 'outgoing'
        : row.classList.contains('message-in') || row.querySelector('.message-in') ? 'incoming'
        : null;

      if (direction === 'outgoing') outgoing++;
      else if (direction === 'incoming') incoming++;
      else unknownDirection++;

      const textNodes = Array.from(row.querySelectorAll(SELECTORS.messageText.tiers[0]));
      const text = textNodes.length
        ? textNodes.map((n) => n.textContent ?? '').join(' ')
        : (row.textContent ?? '');
      if (textNodes.length) withText++;

      const counts = countLetters(text);
      hebrew += counts.hebrew;
      latin += counts.latin;
    }

    const composer = resolve(SELECTORS.composer).element;

    report.reading = {
      rowsFoundTotal: rows.length,
      rowSelectorTier: rowTier,
      sampled: sample.length,
      directions: { outgoing, incoming, unknown: unknownDirection },
      rowsWhereTextSelectorMatched: withText,
      letterTotalsAcrossSample: { hebrew, latin },
      sampleDataIdShape: redact(sample.at(-1)?.getAttribute('data-id') ?? null),
      composerFound: Boolean(composer),
      composerEmpty: composer ? (composer.textContent ?? '').trim().length === 0 : null,
      conversationIdSource:
        sample.some((r) => r.getAttribute('data-id')) ? 'data-id (preferred)'
        : resolve(SELECTORS.conversationHeader, main).element ? 'header title (fallback)'
        : 'NONE - adapter would refuse to decide',
    };
  } else {
    report.reading = { error: 'No conversation panel found. Open a conversation and run again.' };
  }

  const verdict =
    Object.values(report.selectors).some((s) => s.status.startsWith('MISSING')) ? 'BROKEN - required selectors missing'
    : Object.values(report.selectors).some((s) => s.status === 'FALLBACK') ? 'DEGRADED - running on fallback selectors'
    : 'HEALTHY - all preferred selectors matched';

  report.verdict = verdict;

  console.log('%c' + verdict, 'font-weight:bold;font-size:14px');
  console.log(JSON.stringify(report, null, 2));
  return report;
})();
