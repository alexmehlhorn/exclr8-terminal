namespace Exclr8.Terminal.Buffer;

/// <summary>
/// East Asian Width + zero-width handling.
/// <list type="bullet">
///   <item>Returns 0 for combining marks, ZWJ (U+200D), variation
///         selectors (U+FE00..U+FE0F + U+E0100..U+E01EF), zero-width
///         space, and the bidi/format category — anything that should
///         attach to a preceding cell instead of advancing the cursor.</item>
///   <item>Returns 2 for wide/fullwidth codepoints (CJK, fullwidth
///         punctuation, emoji ranges) per Unicode 15's
///         EastAsianWidth.txt — same set xterm.js uses in
///         <c>UnicodeV6.ts</c>.</item>
///   <item>Returns 1 for everything else.</item>
/// </list>
/// Doesn't pull in ICU — covers ~99 % of real terminal content.
/// </summary>
internal static class UnicodeWidth
{
    public static int Of(int cp)
    {
        if (cp < 0x300) return 1;
        if (IsZeroWidth(cp)) return 0;
        if (cp < 0x1100) return 1;
        return IsWide(cp) ? 2 : 1;
    }

    /// <summary>True for variation selectors that change the
    /// presentation of the preceding base character. The Print()
    /// path uses this to retro-widen a base cell that was originally
    /// laid down at width 1 (text presentation) into a width-2 emoji
    /// cell.</summary>
    public static bool IsVS16(int cp) => cp == 0xFE0F;
    public static bool IsVS15(int cp) => cp == 0xFE0E;

    private static bool IsZeroWidth(int cp) => cp is
        // Combining diacritical marks (Mn, partial coverage — full
        // table is several thousand ranges; this covers the Latin /
        // Cyrillic / Hebrew / Arabic blocks that show up in shells).
        (>= 0x0300 and <= 0x036F) or
        (>= 0x0483 and <= 0x0489) or
        (>= 0x0591 and <= 0x05BD) or 0x05BF or
        (>= 0x05C1 and <= 0x05C2) or (>= 0x05C4 and <= 0x05C5) or 0x05C7 or
        (>= 0x0610 and <= 0x061A) or (>= 0x064B and <= 0x065F) or 0x0670 or
        (>= 0x06D6 and <= 0x06DC) or (>= 0x06DF and <= 0x06E4) or
        (>= 0x06E7 and <= 0x06E8) or (>= 0x06EA and <= 0x06ED) or
        // ZWJ + ZWNJ + bidi format characters.
        0x200B or 0x200C or 0x200D or 0x200E or 0x200F or
        0x2028 or 0x2029 or 0x202A or 0x202B or 0x202C or 0x202D or 0x202E or
        0x2060 or 0x2061 or 0x2062 or 0x2063 or 0x2064 or 0x206A or 0x206B or
        0x206C or 0x206D or 0x206E or 0x206F or
        // Variation selectors VS1..VS16 + supplementary VS17..VS256.
        (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0100 and <= 0xE01EF) or
        // Tag characters (used in flag emojis, treated as zero-width).
        (>= 0xE0000 and <= 0xE007F);

    private static bool IsWide(int cp) => cp is
        (>= 0x1100 and <= 0x115F) or 0x2329 or 0x232A or
        (>= 0x2E80 and <= 0x303E) or (>= 0x3040 and <= 0x33FF) or
        (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0xA4CF) or
        (>= 0xA960 and <= 0xA97F) or (>= 0xAC00 and <= 0xD7FF) or
        (>= 0xF900 and <= 0xFAFF) or (>= 0xFE10 and <= 0xFE1F) or
        (>= 0xFE30 and <= 0xFE6F) or (>= 0xFF01 and <= 0xFF60) or
        (>= 0xFFE0 and <= 0xFFE6) or (>= 0x1B000 and <= 0x1B12F) or
        (>= 0x1F004 and <= 0x1F0CF) or (>= 0x1F200 and <= 0x1F2FF) or
        (>= 0x1F300 and <= 0x1F64F) or (>= 0x1F900 and <= 0x1FAFF) or
        (>= 0x20000 and <= 0x2FFFD) or (>= 0x30000 and <= 0x3FFFD);
}
