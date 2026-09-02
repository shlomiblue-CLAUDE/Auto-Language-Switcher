using AutoLang.Core;

namespace AutoLang.Core.Tests;

public class TextNormalizerTests
{
    [Theory]
    [InlineData("https://example.com/some/path?q=hello")]
    [InlineData("http://EXAMPLE.COM")]
    [InlineData("www.example.com/page")]
    [InlineData("someone@example.com")]
    [InlineData("google.com")]
    [InlineData("docs.google.com/document/d/abc")]
    public void Links_and_emails_contribute_no_letters(string text)
    {
        // These are dense with Latin letters but say nothing about how the writer types.
        Assert.Equal(0, TextNormalizer.Analyze(text).Relevant);
    }

    [Fact]
    public void Link_is_removed_but_surrounding_words_survive()
    {
        var stats = TextNormalizer.Analyze("תראה את זה https://example.com/deal מעולה");
        Assert.Equal(0, stats.Of(Language.English));
        Assert.Equal(13, stats.Of(Language.Hebrew));   // תראה(4) + את(2) + זה(2) + מעולה(5)
    }

    [Fact]
    public void Ordinary_words_containing_a_dot_are_not_mistaken_for_domains()
    {
        // A generic \w+\.\w+ pattern would eat this. The TLD allowlist must not.
        var stats = TextNormalizer.Analyze("ok.thanks");
        Assert.Equal(8, stats.Of(Language.English));
    }

    [Theory]
    [InlineData("12345 67.89")]
    [InlineData("!!! ??? ... ,,, ---")]
    [InlineData("   \t\n  ")]
    [InlineData("")]
    public void Digits_punctuation_and_whitespace_are_not_evidence(string text)
    {
        Assert.Equal(0, TextNormalizer.Analyze(text).Relevant);
    }

    [Theory]
    [InlineData("😀😀😀")]
    [InlineData("👍")]
    [InlineData("👨‍👩‍👧‍👦")]          // ZWJ sequence
    [InlineData("👋🏽")]                 // skin tone modifier
    [InlineData("🇮🇱")]                 // regional indicator pair
    public void Emoji_are_not_evidence(string text)
    {
        Assert.Equal(0, TextNormalizer.Analyze(text).Relevant);
    }

    [Fact]
    public void Null_is_handled_like_empty()
    {
        Assert.Equal(0, TextNormalizer.Analyze(null).Relevant);
    }

    [Fact]
    public void Niqqud_is_not_counted_as_letters()
    {
        // "שָׁלוֹם" is 4 letters carrying 4 combining marks. Counting the marks would let one
        // vocalised message outweigh several plain ones.
        var stats = TextNormalizer.Analyze("שָׁלוֹם");
        Assert.Equal(4, stats.Of(Language.Hebrew));
    }

    [Fact]
    public void Hebrew_presentation_forms_count_as_Hebrew()
    {
        var stats = TextNormalizer.Analyze("\uFB4F");   // Hebrew ligature alef-lamed
        Assert.Equal(1, stats.Of(Language.Hebrew));
    }

    [Fact]
    public void Mixed_script_message_counts_both_sides()
    {
        var stats = TextNormalizer.Analyze("שלום hello");
        Assert.Equal(4, stats.Of(Language.Hebrew));
        Assert.Equal(5, stats.Of(Language.English));
    }

    [Fact]
    public void Accented_latin_is_not_counted_in_v1()
    {
        // Documented v1 limitation: the Latin range is A-Za-z per the PDR, so "café" scores 3.
        Assert.Equal(3, TextNormalizer.Analyze("café").Of(Language.English));
    }
}
