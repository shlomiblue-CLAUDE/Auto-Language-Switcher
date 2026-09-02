namespace AutoLang.Core;

/// <summary>
/// One place that answers whether a paid feature is available.
///
/// v1 ships free and every answer is yes. This exists so that stays a decision rather than a
/// rewrite: turning a feature paid later means changing this class, not threading a new concept
/// through the decision engine, the store and the popup.
///
/// It is deliberately not a licensing system. There is no key format, no signature check and no
/// server, because guessing at those now would mean maintaining a design nobody has yet decided
/// on. What it does fix is the shape: features ask this class, and nothing else knows the answer.
///
/// If it ever does gate something, two properties have to survive:
///
///   - No network. The product's central claim is that nothing leaves the machine, and a licence
///     check that phones home would break it more thoroughly than any feature is worth. An
///     offline signed key is the only form that fits.
///   - Failing open. A licence that cannot be read must not stop the keyboard from working. A
///     paid feature quietly reverting to the free behaviour is an annoyance; a product that stops
///     switching because it could not parse a file is a broken product.
/// </summary>
public static class LicenseGate
{
    /// <summary>Named features rather than a single flag, so a future split is not all-or-nothing.</summary>
    public enum Feature
    {
        /// <summary>Sites beyond WhatsApp Web.</summary>
        AdditionalSites,

        /// <summary>Languages beyond Hebrew and English.</summary>
        AdditionalLanguages,

        /// <summary>Desktop applications as a signal source.</summary>
        DesktopSources,
    }

    /// <summary>True while the product is free. See the class comment before changing this.</summary>
    public static bool IsAvailable(Feature feature) => true;

    /// <summary>Shown in the popup. Empty while everything is available.</summary>
    public static string Edition => "";
}
