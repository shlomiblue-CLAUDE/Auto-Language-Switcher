using AutoLang.Core;

namespace AutoLang.Core.Tests;

public class LanguageDetectorTests
{
    private readonly LanguageDetector _detector = new();

    private static List<string?> Repeat(string message, int count) =>
        Enumerable.Repeat<string?>(message, count).ToList();

    // --- PDR section 16 acceptance rows that the detector alone can answer -------------------

    [Fact]
    public void Ten_clear_Hebrew_messages_yield_Hebrew()
    {
        var result = _detector.Detect(Repeat("מה נשמע אחי הכל טוב", 10));

        Assert.Equal(Language.Hebrew, result.Winner);
        Assert.True(result.IsActionable);
        Assert.Equal(DetectionReason.Confident, result.Reason);
        Assert.Equal(1.0, result.Confidence, 3);
    }

    [Fact]
    public void Ten_clear_English_messages_yield_English()
    {
        var result = _detector.Detect(Repeat("hey there how are you doing", 10));

        Assert.Equal(Language.English, result.Winner);
        Assert.True(result.IsActionable);
        Assert.Equal(DetectionReason.Confident, result.Reason);
    }

    [Fact]
    public void Only_the_users_own_messages_matter_when_the_other_side_writes_Hebrew()
    {
        // Acceptance row "הצד השני בשפה אחרת": incoming Hebrew, outgoing English => EN.
        // The detector is fed outgoing messages only, so the incoming side cannot leak in.
        var result = _detector.Detect(Repeat("sure, sending the invoice today", 6));

        Assert.Equal(Language.English, result.Winner);
        Assert.True(result.IsActionable);
    }

    [Fact]
    public void Evenly_mixed_conversation_is_not_actionable()
    {
        var messages = new List<string?>
        {
            "שלום מה נשמע", "hello how are you",
            "בסדר גמור תודה", "all good thanks",
            "נתראה מחר", "see you tomorrow"
        };

        var result = _detector.Detect(messages);

        Assert.False(result.IsActionable);
        Assert.Equal(DetectionReason.LowConfidence, result.Reason);
        Assert.InRange(result.Confidence, 0.40, 0.70);
    }

    [Theory]
    [InlineData("👍👍")]
    [InlineData("12345")]
    [InlineData("https://example.com/a/b/c")]
    [InlineData("!!!")]
    public void Messages_with_no_usable_text_produce_no_decision(string message)
    {
        var result = _detector.Detect(Repeat(message, 5));

        Assert.False(result.IsActionable);
        Assert.Equal(DetectionReason.NoUsableText, result.Reason);
        Assert.Equal(Language.Unknown, result.Winner);
        Assert.Equal(5, result.MessagesSkipped);
    }

    [Fact]
    public void Empty_history_produces_no_decision()
    {
        var result = _detector.Detect([]);

        Assert.Equal(DetectionReason.NoMessages, result.Reason);
        Assert.False(result.IsActionable);
    }

    // --- Thresholds -------------------------------------------------------------------------

    [Fact]
    public void Messages_below_the_minimum_letter_count_are_skipped()
    {
        // "ok" is 2 letters, under MinLettersPerMessage = 3.
        var result = _detector.Detect(["ok", "hi", "no"]);

        Assert.Equal(3, result.MessagesSkipped);
        Assert.Equal(DetectionReason.NoUsableText, result.Reason);
    }

    [Fact]
    public void A_single_short_message_is_confident_but_lacks_evidence_to_act()
    {
        // "yes" is exactly 3 letters: usable, unanimous, but 3 < MinWeightedLetters = 5.
        var result = _detector.Detect(["yes"]);

        Assert.Equal(Language.English, result.Winner);
        Assert.Equal(1.0, result.Confidence, 3);
        Assert.False(result.IsActionable);
        Assert.Equal(DetectionReason.InsufficientEvidence, result.Reason);
    }

    [Fact]
    public void Five_weighted_letters_is_the_acting_boundary()
    {
        var result = _detector.Detect(["hello"]);   // exactly 5 letters at weight 1.0

        Assert.Equal(5.0, result.ScoreOf(Language.English), 3);
        Assert.True(result.IsActionable);
    }

    [Fact]
    public void Confidence_just_below_the_threshold_does_not_act()
    {
        // Two thirds Hebrew is 0.667, under the 0.70 threshold.
        var stats = new List<MessageStats>
        {
            MessageStats.From((Language.Hebrew, 20), (Language.English, 10))
        };

        var result = _detector.Score(stats);

        Assert.Equal(Language.Hebrew, result.Winner);
        Assert.InRange(result.Confidence, 0.66, 0.67);
        Assert.False(result.IsActionable);
        Assert.Equal(DetectionReason.LowConfidence, result.Reason);
    }

    [Fact]
    public void Confidence_at_the_threshold_acts()
    {
        var stats = new List<MessageStats>
        {
            MessageStats.From((Language.Hebrew, 70), (Language.English, 30))
        };

        var result = _detector.Score(stats);

        Assert.Equal(0.70, result.Confidence, 3);
        Assert.True(result.IsActionable);
    }

    // --- Recency ----------------------------------------------------------------------------

    [Fact]
    public void Recent_messages_outweigh_older_ones_of_equal_length()
    {
        // Newest first: three Hebrew, then three English of identical length.
        var messages = new List<string?>
        {
            "אבגדהו", "אבגדהו", "אבגדהו",
            "abcdef", "abcdef", "abcdef"
        };

        var result = _detector.Detect(messages);

        Assert.Equal(Language.Hebrew, result.Winner);
        Assert.True(result.ScoreOf(Language.Hebrew) > result.ScoreOf(Language.English));
    }

    [Fact]
    public void The_newest_usable_message_keeps_full_weight_when_older_ones_are_skipped()
    {
        // A thumbs-up at the top must not demote the newest real message below weight 1.0.
        var withEmoji = _detector.Detect(["👍", "hello"]);
        var without = _detector.Detect(["hello"]);

        Assert.Equal(without.ScoreOf(Language.English), withEmoji.ScoreOf(Language.English), 3);
        Assert.Equal(1, withEmoji.MessagesSkipped);
    }

    [Fact]
    public void One_long_old_message_against_several_short_recent_ones_is_left_alone()
    {
        // Genuinely ambiguous. The right behaviour is to change nothing, not to pick a side.
        var messages = new List<string?>
        {
            "שלומי", "שלומי", "שלומי", "שלומי", "שלומי",
            new string('a', 50)
        };

        var result = _detector.Detect(messages);

        Assert.False(result.IsActionable);
        Assert.Equal(DetectionReason.LowConfidence, result.Reason);
    }

    [Fact]
    public void Only_the_ten_most_recent_messages_are_considered()
    {
        var messages = Repeat("hello there", 10).Concat(Repeat("שלום לכולם", 10)).ToList();

        var result = _detector.Detect(messages);

        Assert.Equal(10, result.MessagesConsidered);
        Assert.Equal(Language.English, result.Winner);
        Assert.Equal(0, result.ScoreOf(Language.Hebrew));
    }

    // --- Transliteration --------------------------------------------------------------------

    [Fact]
    public void Hebrew_typed_in_latin_letters_asks_for_the_English_layout()
    {
        // These are Hebrew words, but the user is pressing Latin keys. The question the detector
        // answers is which keyboard to use, not which language is being spoken.
        var result = _detector.Detect(Repeat("ma nishma achi hakol tov", 5));

        Assert.Equal(Language.English, result.Winner);
        Assert.True(result.IsActionable);
    }

    // --- Purity -----------------------------------------------------------------------------

    [Fact]
    public void Detection_is_deterministic()
    {
        var messages = new List<string?> { "שלום", "hello there", "מה נשמע" };

        var first = _detector.Detect(messages);
        var second = _detector.Detect(messages);

        Assert.Equal(first.ToString(), second.ToString());
    }

    [Fact]
    public void Custom_options_are_honoured()
    {
        var strict = new LanguageDetector(new DetectionOptions { ConfidenceThreshold = 0.95 });
        var stats = new List<MessageStats> { MessageStats.From((Language.Hebrew, 8), (Language.English, 2)) };

        Assert.False(strict.Score(stats).IsActionable);      // 0.80 < 0.95
        Assert.True(new LanguageDetector().Score(stats).IsActionable);
    }
}
