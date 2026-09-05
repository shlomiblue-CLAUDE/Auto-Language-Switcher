using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoLang.Core;

/// <summary>
/// Turns a desktop window into a conversation key, and destroys the title on the way.
///
/// A window title is the desktop equivalent of a chat title, and it is just as identifying: Slack
/// puts the channel or the person in it, a mail client puts the subject, an editor puts the path
/// to a file. So it gets exactly the treatment a WhatsApp chat title already gets - a salted
/// SHA-256, truncated to the shape the Agent accepts - and the raw value never leaves this call.
///
/// The salt is the reason the hash is worth doing. Without one, SHA-256 of "general" is the same
/// value on every machine on earth, and a stored file would say which Slack channels somebody is
/// in to anybody who thought to precompute the obvious few thousand. With a per-install salt the
/// file is meaningless anywhere but the computer that wrote it.
///
/// It reuses the shape the browser side settled on rather than inventing a second one, so
/// AgentCore.IsHashedKey stays a single rule for every source: 32 lowercase hex characters, and
/// anything else is refused.
/// </summary>
public static class DesktopIdentity
{
    /// <summary>Hex characters kept. 128 bits is far beyond collision risk at this scale.</summary>
    private const int KeyLength = 32;

    /// <summary>
    /// The key for one window.
    ///
    /// Process and title together, because neither alone is right: the process alone makes all of
    /// Slack one memory, and the title alone would merge two applications that happen to name a
    /// window the same way.
    /// </summary>
    public static string ForWindow(string salt, string processName, string windowTitle)
    {
        var raw = $"{salt}:{processName.ToLowerInvariant()}|{NormaliseTitle(windowTitle)}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(raw));

        // Lowercase because that is the shape AgentCore.IsHashedKey accepts, from every source.
        return Convert.ToHexString(digest).ToLowerInvariant()[..KeyLength];
    }

    /// <summary>
    /// Removes the parts of a window title that change while the user sits in one window.
    ///
    /// The identity is the title, so anything the title does to itself, the memory does too. Two
    /// shapes were measured doing exactly that:
    ///
    ///   *file.txt - Notepad   the unsaved marker, which appears the moment typing starts
    ///   (10) WhatsApp         an unread counter, which moves with every incoming message
    ///
    /// The first is the damaging one, and running F19 is what found it. Two Notepad windows on two
    /// files produced four conversation keys in twenty-five seconds, and recomputing the hashes
    /// from the live titles matched all four exactly: one key per file saved, another per file
    /// dirty. So the product forgot what it had learned about a document at the instant the user
    /// began writing in it - which is the one moment the memory exists for.
    ///
    /// A leading asterisk is safe to remove because Windows forbids * in a file name, so it can
    /// never be part of one. The counter is removed only in the leading "(n) " form that was
    /// measured.
    ///
    /// What is deliberately kept is the application name at the end - "- Notepad", "- Word". Strip
    /// that and every document in an application collapses into one identity, which is the same
    /// defect inverted and worse: instead of forgetting too often it would confuse two documents
    /// for one. Nothing is added here for a shape that has not been seen on a real window.
    /// </summary>
    public static string NormaliseTitle(string title)
    {
        // Invisible directional marks. Present in every sample taken from a Hebrew system, carrying
        // no meaning, and not guaranteed to be emitted consistently by the application that wrote
        // them - so two spellings of one title would otherwise be two conversations.
        var clean = new string(title.Where(c => !IsBidiControl(c)).ToArray()).Trim();

        // Repeated because the shapes can stack - "(2) *notes.txt - Notepad" is both at once. The
        // loop ends when a pass changes nothing, so no ordering is assumed between them.
        bool changed;
        do
        {
            changed = false;

            if (clean.StartsWith('*'))
            {
                clean = clean.TrimStart('*').TrimStart();
                changed = true;
            }

            var counter = UnreadCounter.Match(clean);
            if (counter.Success)
            {
                clean = clean[counter.Length..];
                changed = true;
            }
        } while (changed && clean.Length > 0);

        // A title that was nothing but noise is still an identity, and an empty one would collide
        // with every other empty one in the same process. Keep what was given.
        return clean.Length > 0 ? clean : title.Trim();
    }

    /// <summary>The leading "(10) " an application writes when it has something unread.</summary>
    private static readonly Regex UnreadCounter = new(@"^\(\d+\)\s+", RegexOptions.Compiled);

    /// <summary>
    /// Marks that steer bidirectional text: LRM, RLM, the embedding and isolate controls.
    /// </summary>
    private static bool IsBidiControl(char c) =>
        c is '‎' or '‏' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩');

    /// <summary>A fresh salt, for a store that has never had one.</summary>
    public static string NewSalt() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
