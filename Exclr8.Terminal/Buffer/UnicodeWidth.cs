namespace Exclr8.Terminal.Buffer;

/// <summary>
/// East Asian Width: returns 2 for wide/fullwidth codepoints, 1 for all
/// others. Ranges derived from Unicode 15's EastAsianWidth.txt — same
/// set xterm.js uses in <c>UnicodeV6.ts</c>. Covers ~99 % of real
/// terminal content (CJK, fullwidth punctuation, emoji) without
/// pulling in ICU.
/// </summary>
internal static class UnicodeWidth
{
    public static int Of(int cp)
    {
        if (cp < 0x1100) return 1;
        return IsWide(cp) ? 2 : 1;
    }

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
