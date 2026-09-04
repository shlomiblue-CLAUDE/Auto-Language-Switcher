using System.Security.Cryptography;
using System.Text;

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
        var raw = $"{salt}:{processName.ToLowerInvariant()}|{windowTitle}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(raw));

        // Lowercase because that is the shape AgentCore.IsHashedKey accepts, from every source.
        return Convert.ToHexString(digest).ToLowerInvariant()[..KeyLength];
    }

    /// <summary>A fresh salt, for a store that has never had one.</summary>
    public static string NewSalt() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
