using AutoLang.Core;

namespace AutoLang.Core.Tests;

/// <summary>
/// What a window title is allowed to change without becoming a different conversation.
///
/// Every title in here was measured on a real window, not invented. Running acceptance row F19 -
/// two Notepad windows on two files - produced four conversation keys in twenty-five seconds, and
/// recomputing the hashes from the live titles matched all four: one per file saved, one per file
/// dirty. The product was forgetting a document the instant somebody typed in it.
///
/// The opposite failure is the one to watch while reading these: normalise too much and two
/// documents become one memory, which is worse, because forgetting is visible and confusing two
/// contexts is not.
/// </summary>
public class DesktopIdentityTests
{
    private const string Salt = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Key(string title) => DesktopIdentity.ForWindow(Salt, "notepad", title);

    [Fact]
    public void Typing_in_a_document_does_not_change_its_identity()
    {
        // The exact pair measured: Notepad prepends * as soon as there are unsaved changes, which
        // is the first keystroke. Windows forbids * in a file name, so it can never be part of one.
        Assert.Equal(Key("טסט1.txt - פנקס רשימות"), Key("*טסט1.txt - פנקס רשימות"));
    }

    [Fact]
    public void Two_documents_in_one_application_stay_two_conversations()
    {
        // The guard against over-normalising. If this ever fails, every file open in an editor
        // shares one memory and the per-window design is gone.
        Assert.NotEqual(Key("Document1 - Word"), Key("Document2 - Word"));
        Assert.NotEqual(Key("*טסט1.txt - פנקס רשימות"), Key("*טסט2.txt - פנקס רשימות"));
    }

    [Fact]
    public void An_unread_counter_does_not_mint_a_new_conversation()
    {
        // Measured on WhatsApp's WebView, which titles itself "(10) WhatsApp". Left alone, every
        // arriving message would start a fresh context and discard what the last one taught.
        Assert.Equal(Key("WhatsApp"), Key("(10) WhatsApp"));
        Assert.Equal(Key("(3) WhatsApp"), Key("(10) WhatsApp"));
    }

    [Fact]
    public void Invisible_direction_marks_do_not_split_a_conversation()
    {
        // Present in every sample taken from a Hebrew system, meaningless to the user, and not
        // guaranteed to be written consistently by the application that emits them.
        Assert.Equal(Key("Claude"), Key("‪Claude‬"));
    }

    [Fact]
    public void The_application_name_is_kept()
    {
        // Deliberately not stripped. The same file name open in two applications is two contexts,
        // and the trailing name is the only thing that says so.
        Assert.NotEqual(Key("notes.txt - Notepad"), Key("notes.txt - Word"));
    }

    [Fact]
    public void A_number_inside_the_name_is_not_a_counter()
    {
        // Only a leading "(n) " is a counter. A file genuinely called "Q1 (2024) plan" keeps it,
        // because cutting it would merge that document with a different one.
        Assert.NotEqual(Key("Q1 (2024) plan.txt - Notepad"), Key("Q1 plan.txt - Notepad"));
        Assert.Equal("Q1 (2024) plan.txt - Notepad", DesktopIdentity.NormaliseTitle("Q1 (2024) plan.txt - Notepad"));
    }

    [Fact]
    public void Stacked_noise_is_removed_in_any_order()
    {
        // Not measured together, but each half was, and an application that does both should not
        // depend on which one this code happens to check first.
        Assert.Equal("notes.txt - Notepad", DesktopIdentity.NormaliseTitle("(2) *notes.txt - Notepad"));
        Assert.Equal("notes.txt - Notepad", DesktopIdentity.NormaliseTitle("*(2) notes.txt - Notepad"));
    }

    [Fact]
    public void A_title_that_is_nothing_but_noise_still_has_an_identity()
    {
        // Normalising to an empty string would collide with every other empty one in the same
        // process, which is the merge failure arriving through the back door.
        Assert.NotEqual(string.Empty, DesktopIdentity.NormaliseTitle("***"));
        Assert.Equal(32, Key("***").Length);
    }

    [Fact]
    public void The_key_is_still_the_shape_the_agent_accepts()
    {
        // Normalisation happens before hashing, so it must not have changed the output contract.
        var key = Key("*טסט1.txt - פנקס רשימות");
        Assert.Equal(32, key.Length);
        Assert.All(key, c => Assert.True(char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f')));
    }

    [Fact]
    public void Different_salts_still_give_different_keys()
    {
        // The privacy claim, unchanged: a stored key means nothing on another machine.
        Assert.NotEqual(
            DesktopIdentity.ForWindow(DesktopIdentity.NewSalt(), "notepad", "notes.txt - Notepad"),
            DesktopIdentity.ForWindow(DesktopIdentity.NewSalt(), "notepad", "notes.txt - Notepad"));
    }
}
