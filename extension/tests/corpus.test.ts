import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { analyze } from '../src/shared/text-normalizer.js';
import type { Language } from '../src/shared/languages.js';

/**
 * The other half of the anti-drift contract. AutoLang.Core.Tests/CorpusParityTests.cs runs this
 * exact file against the C# counter; if the two implementations ever disagree, one of these two
 * suites goes red.
 */

interface CorpusCase {
  id: string;
  text: string;
  counts: Partial<Record<Language, number>>;
}

const corpusPath = fileURLToPath(new URL('../../tests/fixtures/detector-corpus.json', import.meta.url));
const corpus = JSON.parse(readFileSync(corpusPath, 'utf8')) as { cases: CorpusCase[] };

const LANGUAGES: Language[] = ['Hebrew', 'English'];

describe('shared detector corpus', () => {
  it('is not empty', () => {
    expect(corpus.cases.length).toBeGreaterThan(0);
  });

  it('has unique case ids', () => {
    const ids = corpus.cases.map((c) => c.id);
    expect(new Set(ids).size).toBe(ids.length);
  });

  for (const testCase of corpus.cases) {
    it(`${testCase.id} produces the agreed counts`, () => {
      const actual = analyze(testCase.text);
      for (const language of LANGUAGES) {
        expect(actual[language] ?? 0, `${language} count for "${testCase.id}"`).toBe(
          testCase.counts[language] ?? 0,
        );
      }
    });
  }
});
