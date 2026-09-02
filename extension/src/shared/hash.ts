/**
 * Conversation identity hashing.
 *
 * A conversation id is a phone number. PDR section 11 requires that it be hashed with a local
 * random salt, and the salt is what makes the hash worth doing: without it, SHA-256 of a phone
 * number is trivially reversible by iterating a country's number space, and the same contact
 * would produce an identical key on every machine on earth. With a per-install salt, a stored
 * preferences file is meaningless outside the machine that wrote it.
 */

const SALT_KEY = 'installSalt';
const SALT_BYTES = 32;
const KEY_LENGTH = 32; // hex characters retained; 128 bits is far beyond collision risk here

let cachedSalt: string | null = null;

function toHex(bytes: Uint8Array): string {
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
}

/** Reads the install salt, generating it once on first use. */
export async function getSalt(): Promise<string> {
  if (cachedSalt) return cachedSalt;

  const stored = await chrome.storage.local.get(SALT_KEY);
  const existing = stored[SALT_KEY] as string | undefined;
  if (existing) {
    cachedSalt = existing;
    return existing;
  }

  const fresh = toHex(crypto.getRandomValues(new Uint8Array(SALT_BYTES)));
  await chrome.storage.local.set({ [SALT_KEY]: fresh });
  cachedSalt = fresh;
  return fresh;
}

/**
 * Hashes a raw conversation id into the key used everywhere downstream.
 * The raw value must not be stored, logged or sent anywhere.
 */
export async function hashConversationId(rawId: string, salt: string): Promise<string> {
  const data = new TextEncoder().encode(`${salt}:${rawId}`);
  const digest = await crypto.subtle.digest('SHA-256', data);
  return toHex(new Uint8Array(digest)).slice(0, KEY_LENGTH);
}

/** Test seam: drops the cached salt so a fresh one is read. */
export function resetSaltCache(): void {
  cachedSalt = null;
}
