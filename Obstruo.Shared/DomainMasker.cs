namespace Obstruo.Shared;

/// <summary>
/// UI-layer utility for masking domain names before display.
/// The database always stores plaintext. This class is the single
/// point where masking is applied — use it everywhere a domain renders.
///
/// Masking format: keep first char, keep last char, asterisk the middle.
///   pornhub.com     → p*****b.com
///   chaturbate.com  → c********e.com
///   ab.com          → a*.com
///   x.com           → *.com
///
/// Rules by source:
///   obstruo-builtin → always masked, no override
///   custom           → unmasked by default, masked if user toggled preference on
/// </summary>
public static class DomainMasker
{
    /// <summary>
    /// Masks a domain string for display.
    /// Splits on the last dot to isolate the TLD, then masks the name segment.
    /// </summary>
    public static string Mask(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return domain;

        int lastDot = domain.LastIndexOf('.');

        // No dot, or dot is the very first character — mask the whole string
        if (lastDot <= 0)
            return MaskSegment(domain);

        string namePart = domain[..lastDot];   // everything before the last dot
        string tldPart = domain[lastDot..];   // ".com", ".net", ".xxx", etc.

        return MaskSegment(namePart) + tldPart;
    }

    /// <summary>
    /// Applies masking based on the domain's source.
    ///
    /// obstruo-builtin → always masked, ignores callerWantsMask
    /// custom           → masked only if callerWantsMask is true
    ///
    /// Pass callerWantsMask from the user's ui_mask_custom config preference.
    /// </summary>
    public static string MaskBySource(string domain, string source, bool callerWantsMask = false)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return domain;

        bool shouldMask = source == "obstruo-builtin" || callerWantsMask;

        return shouldMask ? Mask(domain) : domain;
    }

    // ── Private ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Masks a single segment (no dots):
    ///   length 1  → *
    ///   length 2  → first + *
    ///   length 3+ → first + (length-2 asterisks) + last
    /// </summary>
    private static string MaskSegment(string segment)
    {
        return segment.Length switch
        {
            0 => segment,
            1 => "*",
            2 => $"{segment[0]}*",
            _ => $"{segment[0]}{new string('*', segment.Length - 2)}{segment[^1]}"
        };
    }
}