/**
 * TypeScript mirror of AutoLang.Core's ScriptTable.
 *
 * This exists only because PDR section 8 forbids message text from leaving the page - not even to
 * a local process. The content script therefore has to count letters itself, which means the same
 * rule is implemented twice, in two languages. tests/fixtures/detector-corpus.json is the contract
 * that keeps them identical; both test suites run it.
 *
 * Any change here needs the same change in agent/AutoLang.Core/ScriptTable.cs.
 */

export type Language = 'Hebrew' | 'English' | 'Russian' | 'Arabic' | 'Greek';

/** Wire tags, matching LanguageExtensions.ToTag in the C# side. */
export const LANGUAGE_TAGS: Record<Language, string> = {
  Hebrew: 'he-IL',
  English: 'en-US',
  Russian: 'ru-RU',
  Arabic: 'ar-SA',
  Greek: 'el-GR',
};

export interface ScriptRange {
  readonly start: number;
  readonly end: number;
}

export interface ScriptDefinition {
  readonly language: Language;
  readonly name: string;
  readonly ranges: readonly ScriptRange[];
}

/** Order matters: ties resolve to the first entry, exactly as the C# side does. */
export const SCRIPT_DEFINITIONS: readonly ScriptDefinition[] = [
  {
    language: 'Hebrew',
    name: 'Hebrew',
    ranges: [
      { start: 0x0590, end: 0x05ff }, // Hebrew block, per PDR section 5
      { start: 0xfb1d, end: 0xfb4f }, // Hebrew presentation forms
    ],
  },
  {
    language: 'English',
    name: 'Latin',
    ranges: [
      { start: 0x0041, end: 0x005a }, // A-Z
      { start: 0x0061, end: 0x007a }, // a-z
    ],
  },
  {
    language: 'Russian',
    name: 'Cyrillic',
    ranges: [
      { start: 0x0400, end: 0x04ff }, // Cyrillic; covers Russian in full
    ],
  },
  {
    language: 'Arabic',
    name: 'Arabic',
    ranges: [
      { start: 0x0600, end: 0x06ff }, // Arabic
      { start: 0x0750, end: 0x077f }, // Arabic Supplement
      // Presentation forms. Deliberately starting at FB50: Hebrew's sit at FB1D-FB4F, just below.
      { start: 0xfb50, end: 0xfdff },
      { start: 0xfe70, end: 0xfeff },
    ],
  },
  {
    language: 'Greek',
    name: 'Greek',
    ranges: [
      { start: 0x0370, end: 0x03ff }, // Greek and Coptic
      { start: 0x1f00, end: 0x1fff }, // Greek Extended, for polytonic text
    ],
  },
];

const LETTER = /\p{L}/u;

/**
 * Which language a code point is evidence for, or null if it is evidence for nothing.
 *
 * The letter test is the important part. The Hebrew block also holds niqqud and cantillation marks,
 * which are \p{Mn} rather than \p{L}. Counting those would let a single vocalised message outweigh
 * several plain ones. The C# side achieves the same thing with char.IsLetter.
 */
export function classifyCodePoint(codePoint: number): Language | null {
  if (!LETTER.test(String.fromCodePoint(codePoint))) return null;

  for (const def of SCRIPT_DEFINITIONS) {
    for (const range of def.ranges) {
      if (codePoint >= range.start && codePoint <= range.end) return def.language;
    }
  }
  return null;
}
