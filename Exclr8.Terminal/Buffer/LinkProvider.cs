using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Exclr8.Terminal.Buffer;

/// <summary>
/// One detected link in a row of buffer cells. Coordinates are
/// 0-based column indices, half-open at <see cref="EndCol"/>; the
/// linked range covers <c>[StartCol, EndCol)</c>. <see cref="Url"/> is
/// what the host opens on click — typically the same string the
/// renderer matched, but providers can rewrite it (e.g. issue numbers
/// → tracker URLs).
/// </summary>
public sealed record TerminalLink(int StartCol, int EndCol, string Url);

/// <summary>
/// Plug-in match-and-link strategy. The renderer hands the provider a
/// row's plain text; the provider returns 0..N spans the host wants
/// to expose as clickable. Providers run on the UI thread per repaint
/// over visible rows only — keep them O(N) and allocation-light.
///
/// <para><b>Coordinates:</b> <see cref="TerminalLink.StartCol"/> /
/// <see cref="TerminalLink.EndCol"/> are <i>string-index</i>
/// coordinates into the rowText the provider was given. The renderer
/// translates these back to cell columns via a parallel column map
/// built when the row text is materialised — astral-plane runes
/// (most emoji, CJK Ext B+) encode as a UTF-16 surrogate pair, two
/// chars in the haystack but one cell, so a literal cell-column
/// would be wrong for any URL after an emoji.</para>
///
/// <para>Built-in <see cref="WebLinkProvider"/> matches
/// <c>https?://</c> URLs. Hosts can register their own (issue
/// numbers, file paths, internal protocols) via
/// <see cref="TerminalControl.RegisterLinkProvider"/>.</para>
/// </summary>
public interface ILinkProvider
{
    IEnumerable<TerminalLink> Provide(string rowText);
}

/// <summary>Helper that materialises a row of cells as a string AND
/// the parallel string-index → cell-column map. Shared by the
/// click-hit path and the renderer underline pass so they agree on
/// how astral runes map between the two coordinate systems.
/// Exposed publicly so hosts that want to write their own
/// renderer-side link decoration get the same coordinate
/// translation for free.</summary>
public static class RowText
{
    /// <summary>Convert <paramref name="cells"/> to a string suitable
    /// for ILinkProvider.Provide. Wide-cell continuation slots emit a
    /// space (cell index advances; string index advances by 1).
    /// Empty cells emit a space. <paramref name="colMap"/> length
    /// equals the returned string's length: <c>colMap[i]</c> is the
    /// cell column corresponding to string index i. Astral runes
    /// occupy two consecutive string indices both pointing at the
    /// same cell column.</summary>
    public static string Build(TerminalCell[] cells, out int[] colMap)
    {
        var sb = new System.Text.StringBuilder(cells.Length);
        // Worst case: every cell is an astral rune (2 chars) → twice the cell count.
        var map = new int[cells.Length * 2];
        int textLen = BuildInto(cells, sb, map);
        if (textLen != map.Length) Array.Resize(ref map, textLen);
        colMap = map;
        return sb.ToString();
    }

    /// <summary>Allocation-free variant for hot rendering paths. The
    /// caller supplies a reusable <paramref name="sb"/> (cleared
    /// before append) and <paramref name="colMap"/> buffer; we return
    /// the populated length. <paramref name="colMap"/> must be at
    /// least <c>cells.Length * 2</c>.
    /// </summary>
    public static int BuildInto(TerminalCell[] cells,
        System.Text.StringBuilder sb, int[] colMap)
    {
        sb.Clear();
        sb.EnsureCapacity(cells.Length);
        int mapLen = 0;
        for (int c = 0; c < cells.Length; c++)
        {
            int rune = cells[c].Rune;
            if ((cells[c].Flags2 & CellFlags2.IsContinuation) != 0)
            {
                // Continuation slot of a wide cell: emit a space so
                // string-index advances 1:1 with cell-column for the
                // tail half of the wide character.
                sb.Append(' ');
                colMap[mapLen++] = c;
                continue;
            }
            if (rune == 0)
            {
                sb.Append(' ');
                colMap[mapLen++] = c;
            }
            else if (rune <= 0xFFFF)
            {
                sb.Append((char)rune);
                colMap[mapLen++] = c;
            }
            else
            {
                sb.Append(char.ConvertFromUtf32(rune));
                colMap[mapLen++] = c;
                colMap[mapLen++] = c;
            }
        }
        return mapLen;
    }

    /// <summary>Cheap prescan: returns true if <paramref name="cells"/>
    /// could plausibly contain a URL (looks for the <c>:</c> + two
    /// adjacent <c>/</c> rune pattern that <c>http://</c>,
    /// <c>https://</c>, <c>file://</c>, <c>ssh://</c>, etc. all share).
    /// Lets the renderer skip the per-row text materialise +
    /// regex run when no row could possibly match.</summary>
    public static bool MightContainUrl(TerminalCell[] cells)
    {
        // Scan for ":/" — necessary substring of every "scheme://"
        // prefix. False positives (e.g. ":/" in code) cost a regex
        // run; false negatives would silently break link detection.
        // Stop one before the end so the lookahead is in-bounds.
        for (int i = 0; i < cells.Length - 1; i++)
        {
            if (cells[i].Rune == ':' && cells[i + 1].Rune == '/')
                return true;
        }
        return false;
    }
}

/// <summary>Default <c>https?://</c> matcher. Stops at whitespace and
/// control characters; trailing sentence punctuation
/// (<c>.,;:?!)>]'"</c>) is stripped so "see https://example.com."
/// doesn't capture the dot.
/// </summary>
public sealed class WebLinkProvider : ILinkProvider
{
    // Compiled once. The character class uses explicit \xNN escapes
    // for the control range — earlier iterations of this file
    // contained literal NUL/control bytes in the source, which made
    // some tools detect the file as binary. Verbatim string + regex
    // \x00-\x1F syntax keeps both the source and the compiled match
    // identical to what we want.
    private static readonly Regex Pattern = new(
        @"https?://[^\s\x00-\x1F\x7F]+",
        RegexOptions.Compiled);

    private static readonly char[] TrailingPuncts = { '.', ',', ';', ':', '?', '!', ')', '>', ']', '\'', '"' };

    public IEnumerable<TerminalLink> Provide(string rowText)
    {
        if (string.IsNullOrEmpty(rowText)) yield break;
        foreach (Match m in Pattern.Matches(rowText))
        {
            int start = m.Index;
            int end   = m.Index + m.Length;
            // Strip trailing punctuation that's clearly part of the
            // surrounding sentence rather than the URL.
            while (end > start && Array.IndexOf(TrailingPuncts, rowText[end - 1]) >= 0)
                end--;
            if (end <= start + "https://".Length) continue;
            yield return new TerminalLink(start, end, rowText[start..end]);
        }
    }
}
