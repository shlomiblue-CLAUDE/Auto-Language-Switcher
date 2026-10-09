/**
 * Generic-adapter identity probe.
 *
 * Paste into the DevTools console on the page itself - a spreadsheet, a document, any site the
 * generic adapter handles. Not the service worker.
 *
 * WHY THIS EXISTS
 *
 * The sibling probe was written for WhatsApp, where identity comes from a chat title. The generic
 * adapter builds it from three different things, and a month of live log says one of them does not
 * hold still on a spreadsheet:
 *
 *   docs.google.com    80 keys | seen twice or less:  56% | seen on more than one day:  5%
 *   web.whatsapp.com  242 keys | seen twice or less:   2% | seen on more than one day: 77%
 *
 * A chat is recognised on a later day three times in four. A sheet, one time in twenty. Whatever
 * the product learns there is written against a key it will not see again, which is the failure
 * field-identity.ts names in its own comments - "a field whose identity changes learns constantly
 * and remembers nothing, failing quietly, which is the worst way to fail."
 *
 * What is not yet known is WHICH of the three varies. Guessing is how the September adapter
 * breakage happened, so this prints the inputs instead of a conclusion.
 *
 * It deliberately does not reimplement fieldIdentity. A copy here would drift from the real one and
 * then be trusted; what is reported is what the real one is given.
 *
 * WHAT IT PRINTS
 *
 * Nothing readable. Letters become x, digits become #, punctuation and length survive, so a label
 * that reads "A1" and one that reads "B7" are visibly the same shape while a label that reads
 * "Sheet1" is visibly a different one. That is enough to find the variation and consistent with a
 * product whose claim is that nothing identifying leaves the machine.
 *
 * HOW TO USE
 *
 *   1. Open the sheet, F12, Console, on the page. Context selector on `top`.
 *   2. Paste this whole file and press Enter. Chrome blocks the first paste into a console and
 *      asks you to type  allow pasting  first; do that, then paste again.
 *   3. Click into four or five different cells, running  autolangFieldSample()  after each. Type a
 *      word in one. Leave the tab and come back.
 *   4. Run  autolangFieldReport()  and send the table.
 *
 * Step 3 calls the sampler by hand on purpose. A grid on a canvas may keep one hidden editor
 * focused throughout and never fire focusin, and a one-row table would then be ambiguous between
 * "the identity held still" and "nothing was sampled".
 *
 * What the answer looks like: if the `field` column changes while `scope` holds still, identity is
 * per-cell and memory can never replay. If `scope` changes, the document itself is being renamed
 * underneath us - which a brand-new sheet does, moving from /create to its real id.
 */

(() => {
  /** The same order of preference as describeField, so what is read here is what is used there. */
  const ATTRIBUTES = ['aria-label', 'name', 'id', 'data-testid', 'placeholder'];

  /** Letters to x, digits to #. Spacing, punctuation and length survive; nothing else does. */
  const mask = (text) =>
    text
      .replace(/\p{Lu}/gu, 'X')
      .replace(/\p{Ll}|\p{Lo}/gu, 'x')
      .replace(/\p{Nd}/gu, '#');

  /** Mirrors documentScope: path plus the parameterless part of the fragment, query dropped. */
  const scopeOf = () => {
    const path = location.pathname.replace(/\/+$/, '');
    const fragment = location.hash
      .replace(/^#/, '')
      .split('&')
      .filter((part) => part.length > 0 && !part.includes('='))
      .join('&');
    return fragment ? `${path}#${fragment}` : path;
  };

  const samples = [];
  let previous = null;

  function sample(trigger) {
    const active = document.activeElement;

    let field = null;
    let attribute = null;
    if (active instanceof Element) {
      for (const name of ATTRIBUTES) {
        const raw = active.getAttribute(name);
        if (raw && raw.trim()) {
          attribute = name;
          field = raw.trim().replace(/\s+/g, ' ').slice(0, 120);
          break;
        }
      }
    }

    const scope = scopeOf();

    // The scope is masked only in its last segment. A document id is not readable English and
    // seeing that it held still is the whole point, while a path can carry a document title.
    const segments = scope.split('/');
    const shownScope = segments
      .map((part, i) => (i === segments.length - 1 ? mask(part) : part.length > 24 ? `<${part.length} chars>` : part))
      .join('/');

    const row = {
      at: new Date().toTimeString().slice(0, 8),
      trigger,
      tag: active instanceof Element ? active.tagName.toLowerCase() : '(none)',
      scope: shownScope,
      scopeLength: scope.length,
      attribute: attribute ?? '(none - belongs to the document)',
      field: field ? mask(field) : '(none)',
      fieldLength: field ? field.length : 0,
    };

    const fingerprint = `${scope}|${attribute}|${field}`;
    row.changed = previous !== null && fingerprint !== previous;
    previous = fingerprint;

    samples.push(row);
    if (row.changed) console.log('[autolang] identity changed', row);
  }

  document.addEventListener('focusin', () => sample('focusin'), { passive: true });
  document.addEventListener('input', () => sample('input'), { passive: true });
  window.addEventListener('focus', () => sample('window focus'), { passive: true });
  document.addEventListener('visibilitychange', () => sample('visibility'));

  sample('start');

  /**
   * Takes a reading on demand.
   *
   * Needed because this cannot assume clicking a cell moves focus in the DOM at all. A grid drawn
   * on a canvas may keep one hidden editor focused the whole time and never fire focusin, and then
   * a table with a single row in it would be ambiguous between "identity held still" and "nothing
   * was sampled" - which are opposite answers. Calling this after each cell removes the ambiguity.
   */
  window.autolangFieldSample = () => {
    sample('manual');
    return samples[samples.length - 1];
  };

  window.autolangFieldReport = () => {
    const distinct = new Set(samples.map((r) => `${r.scope}|${r.attribute}|${r.field}`));
    console.table(samples);
    console.log(
      `${samples.length} observation(s), ${distinct.size} distinct identit${distinct.size === 1 ? 'y' : 'ies'}. ` +
        'More than one on a single document means nothing learned here is ever found again.',
    );
    const scopes = new Set(samples.map((r) => r.scope));
    const fields = new Set(samples.map((r) => `${r.attribute}|${r.field}`));
    console.log(`  distinct scopes: ${scopes.size}   distinct field descriptors: ${fields.size}`);
    return { observations: samples.length, identities: distinct.size, scopes: scopes.size, fields: fields.size };
  };

  console.log('[autolang] field identity probe armed. Click through a few cells, then run autolangFieldReport()');
})();
