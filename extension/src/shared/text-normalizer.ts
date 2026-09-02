import { classifyCodePoint, type Language } from './languages.js';

/**
 * Per-language letter counts for one message. This - never the text - is what leaves the page.
 * Mirrors AutoLang.Core.MessageStats.
 */
export type LetterCounts = Partial<Record<Language, number>>;

export function relevantLetters(counts: LetterCounts): number {
  return Object.values(counts).reduce((sum, n) => sum + (n ?? 0), 0);
}

// Only spans that carry letters while saying nothing about how the writer types need removing.
// Digits, emoji, punctuation and whitespace are already ignored, because a character is counted
// only when it is a letter in a known script.
const SCHEME_URL = /\b(?:https?|ftp):\/\/\S+/gi;
const WWW_URL = /\bwww\.\S+/gi;
const EMAIL = /\S+@\S+\.\S+/g;

// The TLD allowlist is what keeps "ok.thanks" from being read as a domain. A generic
// \w+\.\w+ pattern would eat ordinary text.
const BARE_DOMAIN =
  /\b(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+(?:com|org|net|io|me|co|il|gov|edu|info|biz|app|dev|ai|ly|uk|de|fr)\b(?:\/\S*)?/gi;

export function strip(text: string | null | undefined): string {
  if (!text) return '';
  return text
    .replace(SCHEME_URL, ' ')
    .replace(WWW_URL, ' ')
    .replace(EMAIL, ' ')
    .replace(BARE_DOMAIN, ' ');
}

/**
 * Counts letters per language. Iterating a string with for..of yields whole code points, so
 * surrogate pairs stay intact and emoji are never miscounted as two characters.
 */
export function analyze(text: string | null | undefined): LetterCounts {
  const cleaned = strip(text);
  if (cleaned.length === 0) return {};

  const counts: LetterCounts = {};
  for (const char of cleaned) {
    const language = classifyCodePoint(char.codePointAt(0)!);
    if (language) counts[language] = (counts[language] ?? 0) + 1;
  }
  return counts;
}
