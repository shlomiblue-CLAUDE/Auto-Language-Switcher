using System.Text.Json;
using AutoLang.Core;

namespace AutoLang.Core.Tests;

/// <summary>
/// Runs the shared corpus against the C# counter.
///
/// The browser content script has to count letters itself, because PDR section 8 forbids sending
/// message text anywhere - even to a local process. That means the same normalisation exists twice,
/// in C# and in TypeScript, and two implementations of one rule drift. This corpus is the guard:
/// the TypeScript test suite added in phase 2 loads the same file and must produce the same numbers.
/// </summary>
public class CorpusParityTests
{
    public sealed record CorpusCase(string Id, string Text, Dictionary<string, int> Counts);

    private sealed record Corpus(string Description, List<CorpusCase> Cases);

    private static readonly Lazy<List<CorpusCase>> Cases = new(Load);

    private static List<CorpusCase> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "detector-corpus.json");
        Assert.True(File.Exists(path), $"Corpus not found at {path}. Check the csproj copies it to output.");

        var corpus = JsonSerializer.Deserialize<Corpus>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(corpus);
        Assert.NotEmpty(corpus!.Cases);
        return corpus.Cases;
    }

    public static TheoryData<string> CaseIds()
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases.Value) data.Add(c.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void Corpus_case_produces_the_agreed_counts(string id)
    {
        var testCase = Cases.Value.Single(c => c.Id == id);
        var actual = TextNormalizer.Analyze(testCase.Text);

        foreach (var language in Enum.GetValues<Language>())
        {
            if (language == Language.Unknown) continue;
            int expected = testCase.Counts.GetValueOrDefault(language.ToString(), 0);
            Assert.Equal(expected, actual.Of(language));
        }
    }

    [Fact]
    public void Every_corpus_case_has_a_unique_id()
    {
        var duplicates = Cases.Value.GroupBy(c => c.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(duplicates);
    }
}
