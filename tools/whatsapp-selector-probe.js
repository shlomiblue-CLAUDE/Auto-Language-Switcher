/**
 * WhatsApp selector probe.
 *
 * Paste into the DevTools console on an open WhatsApp Web conversation. It reports whether the
 * adapter would still understand the page, so DOM drift is caught before users hit it.
 *
 * Rewritten 3 September 2026, after the previous version reported a healthy-looking result against
 * a page the adapter could not read at all. It matched every structural selector it looked for and
 * called that success, while direction — the one reading the product actually depends on — had
 * silently moved. So this version asks the question that matters: on a page with messages, can the
 * adapter tell who sent them?
 *
 * The output is safe to share. Message text is read only to count letters by script, and counts
 * are all it prints — no message content, no contact names, and identifiers reduced to their shape.
 * Read the code before running it; that is the point of shipping it as readable source.
 *
 * Usage:
 *   1. Open https://web.whatsapp.com and select a conversation.
 *   2. F12 -> Console. Chrome may require you to type "allow pasting" first.
 *   3. Paste this whole file and press Enter.
 *   4. Copy the printed JSON.
 */

(() => {
  const SELECTORS = {
    mainPanel: { required: true, tiers: ['#main', '[data-testid="conversation-panel-wrapper"]'] },
    messagesPanel: {
      required: true,
      tiers: [
        '[data-testid="conversation-panel-messages"]',
        '#main div.copyable-area',
        '#main div[role="application"]',
        '#main',
      ],
    },
    conversationTitle: {
      required: false,
      tiers: [
        '[data-testid="conversation-info-header-chat-title"]',
        '#main header [data-testid="conversation-info-header"] span[dir="auto"]',
        '#main header span[dir="auto"]',
        '#main header',
      ],
    },
    messageRow: { required: false, tiers: ['#main [role="row"]', '#main div[data-id]'] },
    messageBubble: { required: false, tiers: ['[data-testid="msg-container"]', '.copyable-text', '[data-id]'] },
    messageText: {
      required: false,
      tiers: ['span.selectable-text span', 'span.selectable-text', '.copyable-text span[dir]', '[dir="auto"]'],
    },
    composer: {
      required: true,
      tiers: [
        '[data-testid="conversation-compose-box-input"]',
        '#main footer div[contenteditable="true"]',
        '#main footer [role="textbox"]',
        'div[contenteditable="true"][role="textbox"]',
      ],
    },
  };

  // Tier order, not document order. Using a comma-joined selector instead would return whichever
  // element comes first in the document, which during development meant measuring the row rather
  // than the bubble and losing geometry entirely.
  const resolve = (spec, root = document) => {
    for (let tier = 0; tier < spec.tiers.length; tier++) {
      const el = root.querySelector(spec.tiers[tier]);
      if (el) return { el, tier, selector: spec.tiers[tier] };
    }
    return { el: null, tier: -1, selector: null };
  };

  const SELF_MARKERS = [/^\s*את\/ה\s*:/, /^\s*You\s*:/i, /^\s*Вы\s*:/i, /^\s*أنت\s*:/];
  const MIN_GAP_DIFFERENCE_PX = 24;

  const LETTER = /\p{L}/u;
  const inRange = (cp, a, b) => cp >= a && cp <= b;
  const countLetters = (text) => {
    let hebrew = 0, latin = 0;
    for (const ch of text ?? '') {
      const cp = ch.codePointAt(0);
      if (!LETTER.test(ch)) continue;
      if (inRange(cp, 0x0590, 0x05ff) || inRange(cp, 0xfb1d, 0xfb4f)) hebrew++;
      else if (inRange(cp, 0x41, 0x5a) || inRange(cp, 0x61, 0x7a)) latin++;
    }
    return { hebrew, latin };
  };

  const redact = (v) => (v == null ? null : String(v).replace(/\d/g, '#').replace(/[A-Za-z0-9]{8,}/g, '<id>'));

  const report = { probeVersion: 2, adapterVersion: '2.0.0', url: location.origin, selectors: {} };

  for (const [name, spec] of Object.entries(SELECTORS)) {
    const { tier, selector } = resolve(spec);
    report.selectors[name] = {
      matchedTier: tier,
      matchedSelector: selector,
      status:
        tier === 0 ? 'preferred'
        : tier > 0 ? 'FALLBACK'
        : spec.required ? 'MISSING (required)'
        : 'missing',
    };
  }

  const main = resolve(SELECTORS.mainPanel).el;
  const panel = resolve(SELECTORS.messagesPanel).el;

  if (!main || !panel) {
    report.reading = { error: 'No conversation panel found. Open a conversation and run again.' };
    report.verdict = 'BROKEN - no conversation panel';
    console.log('%c' + report.verdict, 'font-weight:bold;font-size:14px');
    console.log(JSON.stringify(report, null, 2));
    return report;
  }

  const panelRect = panel.getBoundingClientRect();
  const panelDirection = getComputedStyle(panel).direction;
  // WhatsApp draws the user's own messages on their side, which mirrors in a right-to-left UI.
  const ownSide = panelDirection === 'rtl' ? 'left' : 'right';

  const byGeometry = (bubble) => {
    const r = bubble.getBoundingClientRect();
    if (r.width <= 0 || panelRect.width <= 0) return null;
    const leftGap = r.left - panelRect.left;
    const rightGap = panelRect.right - r.right;
    if (Math.abs(leftGap - rightGap) < MIN_GAP_DIFFERENCE_PX) return null;
    return (leftGap < rightGap ? 'left' : 'right') === ownSide ? 'outgoing' : 'incoming';
  };

  const bySenderLabel = (row) => {
    for (const el of row.querySelectorAll('[aria-label]')) {
      const v = el.getAttribute('aria-label') ?? '';
      if (!v.trim().endsWith(':')) continue;
      return SELF_MARKERS.some((m) => m.test(v)) ? 'outgoing' : 'incoming';
    }
    return null;
  };

  // tail-out marks the OTHER person's messages. Measured, not assumed - see direction.ts.
  const byTail = (row) =>
    row.querySelector('[data-testid="tail-out"]') ? 'incoming'
    : row.querySelector('[data-testid="tail-in"]') ? 'outgoing'
    : null;

  const { tiers: rowTiers } = SELECTORS.messageRow;
  let rows = [], rowTier = -1;
  for (let t = 0; t < rowTiers.length; t++) {
    const found = Array.from(document.querySelectorAll(rowTiers[t]));
    if (found.length) { rows = found; rowTier = t; break; }
  }

  const sample = rows.slice(-10);
  const by = { geometry: 0, 'sender-label': 0, tail: 0, none: 0 };
  const cross = { geometryVsLabel: { agree: 0, disagree: 0 }, geometryVsTail: { agree: 0, disagree: 0 } };
  let bubbles = 0, outgoing = 0, incoming = 0, withText = 0, hebrew = 0, latin = 0;
  let ambiguousGeometry = 0;

  for (const row of sample) {
    const bubble = resolve(SELECTORS.messageBubble, row).el;
    // A row with no bubble is a date divider or a system notice, not a message.
    if (!bubble) continue;
    bubbles++;

    const g = byGeometry(bubble);
    const l = bySenderLabel(row);
    const t = byTail(row);

    if (!g) ambiguousGeometry++;
    if (g && l) cross.geometryVsLabel[g === l ? 'agree' : 'disagree']++;
    if (g && t) cross.geometryVsTail[g === t ? 'agree' : 'disagree']++;

    const direction = g ?? l ?? t;
    by[g ? 'geometry' : l ? 'sender-label' : t ? 'tail' : 'none']++;
    if (direction === 'outgoing') outgoing++;
    else if (direction === 'incoming') incoming++;

    const nodes = Array.from(row.querySelectorAll(SELECTORS.messageText.tiers[0]));
    if (nodes.length) withText++;
    const c = countLetters(nodes.length ? nodes.map((n) => n.textContent ?? '').join(' ') : row.textContent ?? '');
    hebrew += c.hebrew;
    latin += c.latin;
  }

  const composer = resolve(SELECTORS.composer).el;
  const resolved = outgoing + incoming;

  report.reading = {
    panelDirection,
    ownSide,
    rowsFoundTotal: rows.length,
    rowSelectorTier: rowTier,
    sampled: sample.length,
    rowsWithBubbles: bubbles,
    directions: { outgoing, incoming, unknown: bubbles - resolved },
    determinedBy: by,
    ambiguousGeometry,
    crossChecks: cross,
    rowsWhereTextSelectorMatched: withText,
    letterTotalsAcrossSample: { hebrew, latin },
    sampleDataIdShape: redact(sample.at(-1)?.getAttribute('data-id') ?? null),
    composerFound: Boolean(composer),
    composerEmpty: composer ? (composer.textContent ?? '').trim().length === 0 : null,
    conversationIdSource: resolve(SELECTORS.conversationTitle).el
      ? 'header title (the chat JID no longer exists in the DOM)'
      : 'NONE - the adapter would refuse to decide',
  };

  // Direction is judged separately from structure, because the two failed independently last time.
  const structurallyBroken = Object.values(report.selectors).some((s) => s.status.startsWith('MISSING'));
  const directionBroken = bubbles > 0 && resolved === 0;
  const directionPartial = bubbles > 0 && resolved > 0 && resolved < bubbles;
  const contradiction = cross.geometryVsLabel.disagree > 0 || cross.geometryVsTail.disagree > 0;

  report.verdict =
    structurallyBroken ? 'BROKEN - required selectors missing'
    : directionBroken ? 'BROKEN - no message direction could be determined'
    : contradiction ? 'BROKEN - direction signals contradict each other'
    : directionPartial ? 'DEGRADED - some messages have no readable direction'
    : Object.values(report.selectors).some((s) => s.status === 'FALLBACK') ? 'DEGRADED - running on fallback selectors'
    : 'HEALTHY - all preferred selectors matched and every message direction resolved';

  console.log('%c' + report.verdict, 'font-weight:bold;font-size:14px');
  console.log(JSON.stringify(report, null, 2));
  return report;
})();
