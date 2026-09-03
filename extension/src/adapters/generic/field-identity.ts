/**
 * A stable name for one writing field.
 *
 * This is the generic answer to the question WhatsApp answers with a chat title: what is the thing
 * the user's remembered language belongs to? On an ordinary site it is the box they type in. Two
 * boxes on the same site are two different habits - a Hebrew message and an English search live one
 * above the other on the same page - so identity has to reach below the origin.
 *
 * The only real requirement is that the same box yields the same string across reloads. Nothing
 * else depends on it being meaningful, and it never leaves the page unhashed: the caller passes the
 * result through the salted hash in shared/hash.ts, exactly as it does a phone number. That is what
 * makes it safe to prefer an aria-label that may well read "Message to Yossi".
 */

/** Long enough to stay distinctive, short enough that a stray label cannot dominate. */
const MAX_DESCRIPTOR = 120;

/** How far up the tree the structural fallback walks before it stops being stable. */
const MAX_DEPTH = 4;

/**
 * Values that identify this render rather than this field.
 *
 * React, Radix and friends mint ids like `:r7:` or `radix-:r3:` fresh on every mount, and a
 * long hex string is almost always a build artefact. Accepting one would mean a field whose
 * identity changes on every page load, so it would learn constantly and remember nothing -
 * failing quietly, which is the worst way to fail. Rejecting a good id costs only a fall
 * through to the next candidate.
 */
function looksGenerated(value: string): boolean {
  return (
    /:[a-z0-9]+:/i.test(value) ||
    /^[0-9a-f]{8,}$/i.test(value) ||
    /^[0-9a-f]{8}-[0-9a-f]{4}-/i.test(value) ||
    /^\d+$/.test(value)
  );
}

function candidate(element: Element, attribute: string): string | null {
  const raw = element.getAttribute(attribute);
  if (!raw) return null;

  const value = raw.trim().replace(/\s+/g, ' ');
  if (value.length === 0 || looksGenerated(value)) return null;

  return `${attribute}=${value.slice(0, MAX_DESCRIPTOR)}`;
}

/**
 * Position in the tree, used when the field carries no name of its own.
 *
 * Bounded on purpose. A full path from the root is more unique and far less stable: sites wrap
 * content in extra layers between visits, and every wrapper would invent a new field. Four levels
 * is enough to separate the two textareas on a page and shallow enough to survive a redesign of
 * everything above them.
 */
function structuralPath(element: Element): string {
  const parts: string[] = [];
  let node: Element | null = element;

  for (let depth = 0; node && depth < MAX_DEPTH && node !== document.body; depth++) {
    const parent: Element | null = node.parentElement;

    let index = 1;
    if (parent) {
      for (const sibling of Array.from(parent.children)) {
        if (sibling === node) break;
        if (sibling.tagName === node.tagName) index++;
      }
    }

    const role = node.getAttribute('role');
    parts.unshift(`${node.tagName.toLowerCase()}${role ? `[${role}]` : ''}:${index}`);
    node = parent;
  }

  return `path=${parts.join('/')}`;
}

/**
 * Describes a field, in descending order of how likely the answer is to survive a reload.
 *
 * An accessible name comes first because it is written by a human for a human and therefore
 * changes when the field's purpose changes, which is exactly when we want to forget. The
 * structural path is last because it is the only one that always exists.
 */
export function describeField(element: Element): string {
  return (
    candidate(element, 'aria-label') ??
    candidate(element, 'name') ??
    candidate(element, 'id') ??
    candidate(element, 'data-testid') ??
    candidate(element, 'placeholder') ??
    structuralPath(element)
  );
}

/** Long enough for a mail client's thread id, short enough not to grow without bound. */
const MAX_SCOPE = 120;

/**
 * Which document within a site the field belongs to.
 *
 * Without this, every writing box in an application is one memory, and a mail client makes that
 * immediately wrong: every reply box in Gmail carries the same `aria-label`, so all of Gmail
 * collapsed into a single remembered language. A live log showed one key switching to English,
 * then to Hebrew, then back, always from ConversationMemory - the language of the message actually
 * open was never consulted again, because memory outranks analysis by design. It worked once and
 * then answered with whatever had been typed last, forever.
 *
 * The path and the fragment identify the document; the query string is deliberately left out,
 * because that is where transient input lives. `?q=hello` on a search page is one search among
 * many in the same box, and including it would give that box a fresh memory for every query - the
 * opposite failure, and just as silent.
 *
 * Some sites will fragment more than they need to. That degrades to reading the page afresh each
 * time, which is a decision made on current evidence. The failure it replaces was a decision made
 * on evidence from a different conversation.
 */
export function documentScope(location: { pathname: string; hash: string }): string {
  const path = location.pathname.replace(/\/+$/, '');
  const fragment = location.hash.replace(/^#/, '');
  const scope = fragment ? `${path}#${fragment}` : path;

  return scope.slice(0, MAX_SCOPE);
}

/**
 * The raw conversation id for a generic field: which site, which document, and which box.
 *
 * Raw in the sense that site-adapter.ts means it - it may carry a contact's name or a thread id
 * and must be hashed before it goes anywhere. That it is hashed with a per-install salt is what
 * makes it safe to put a URL fragment in here at all.
 */
export function fieldIdentity(
  location: { origin: string; pathname: string; hash: string },
  element: Element,
): string {
  return `${location.origin}${documentScope(location)}|${describeField(element)}`;
}
