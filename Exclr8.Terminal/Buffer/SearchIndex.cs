using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Exclr8.Terminal.Buffer;

/// <summary>Find-mode flags. Default values reproduce the legacy
/// case-insensitive plain-text behaviour. Combine with the bitwise
/// or-equivalent record-with syntax: <c>SearchOptions.Default with
/// { CaseSensitive = true }</c>.</summary>
public sealed record SearchOptions
{
    /// <summary>Match case (default false — case-insensitive).</summary>
    public bool CaseSensitive { get; init; }
    /// <summary>Only match when neighbouring characters are non-word
    /// (whitespace, punctuation, edge of row). Default false.</summary>
    public bool WholeWord { get; init; }
    /// <summary>Treat the needle as a regular expression. Invalid
    /// patterns produce zero matches rather than throwing. Default
    /// false.</summary>
    public bool Regex { get; init; }

    public static readonly SearchOptions Default = new();
}

/// <summary>A single case-insensitive match in the buffer. <see cref="Row"/>
/// is absolute (0 = oldest scrollback row). <see cref="Col"/> and
/// <see cref="Length"/> are cell coordinates — astral-plane runes that
/// encode as surrogate pairs in the haystack map back to a single cell
/// via the column map built by the scanner.</summary>
public readonly record struct SearchMatch(int Row, int Col, int Length);

/// <summary>
/// Search-state holder. Owns the current needle, the match list, and
/// the current match index; exposes <see cref="Set"/>/<see cref="Clear"/>
/// for the buffer to swap state atomically after an off-thread scan,
/// and a static <see cref="Scan"/> that runs case-insensitively against
/// a row snapshot (safe to call on the threadpool).
///
/// <para>Separated from <see cref="TerminalBuffer"/> so the UI-thread /
/// background-thread contract is explicit — the buffer snapshots rows
/// on the UI thread, the snapshot is scanned off-thread via <see cref="Scan"/>,
/// and the results are applied on the UI thread via <see cref="Set"/>.</para>
/// </summary>
internal sealed class SearchIndex
{
    private List<SearchMatch> _matches = new();

    public string? Needle { get; private set; }
    public IReadOnlyList<SearchMatch> Matches => _matches;
    public int CurrentIndex { get; private set; } = -1;

    /// <summary>Replace match state atomically. <paramref name="viewBottomAbs"/>
    /// is used to pick the match closest to the current viewport so
    /// "next" naturally moves forward from where the user is looking.
    ///
    /// <para><b>Ownership:</b> this method takes ownership of
    /// <paramref name="matches"/> — callers must not mutate or hold
    /// references to the passed list after the call. Every current
    /// caller passes a freshly-built List from <see cref="Scan"/>,
    /// so swapping the reference beats an O(N) copy for big result
    /// sets (a 5000-line scrollback with many hits can easily produce
    /// tens of thousands of matches).</para></summary>
    public void Set(string? needle, List<SearchMatch> matches, int viewBottomAbs)
    {
        Needle = string.IsNullOrEmpty(needle) ? null : needle;
        _matches = matches;
        CurrentIndex = _matches.Count > 0 ? NearestIndex(viewBottomAbs) : -1;
    }

    public void Clear()
    {
        Needle = null;
        _matches.Clear();
        CurrentIndex = -1;
    }

    public void Next()
    {
        if (_matches.Count == 0) return;
        CurrentIndex = (CurrentIndex + 1) % _matches.Count;
    }

    public void Prev()
    {
        if (_matches.Count == 0) return;
        CurrentIndex = (CurrentIndex - 1 + _matches.Count) % _matches.Count;
    }

    /// <summary>Absolute row of the currently-selected match, or null if
    /// there is none.</summary>
    public int? CurrentRow =>
        CurrentIndex >= 0 && CurrentIndex < _matches.Count
            ? _matches[CurrentIndex].Row : null;

    private int NearestIndex(int absRowNear)
    {
        int best = 0, bestDist = int.MaxValue;
        for (int i = 0; i < _matches.Count; i++)
        {
            int d = Math.Abs(_matches[i].Row - absRowNear);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    /// <summary>Scan a row snapshot for occurrences of
    /// <paramref name="needle"/>. <paramref name="options"/> selects
    /// case-sensitivity, whole-word matching, and regex. Checks
    /// <paramref name="ct"/> between rows so a superseded search
    /// returns quickly. Safe to run off-thread against a snapshot
    /// captured on the UI thread.</summary>
    public static List<SearchMatch> Scan(
        TerminalCell[][] rows, string needle,
        SearchOptions options, CancellationToken ct)
    {
        var matches = new List<SearchMatch>();
        Regex? rx = null;
        if (options.Regex)
        {
            try
            {
                var rxOpts = RegexOptions.CultureInvariant;
                if (!options.CaseSensitive) rxOpts |= RegexOptions.IgnoreCase;
                rx = new Regex(needle, rxOpts);
            }
            catch (ArgumentException)
            {
                // Invalid regex — return no matches rather than throw.
                return matches;
            }
        }
        for (int r = 0; r < rows.Length; r++)
        {
            ct.ThrowIfCancellationRequested();
            var row = rows[r];
            if (row != null) FindInRow(row, r, needle, options, rx, matches);
        }
        return matches;
    }

    /// <summary>Legacy two-arg form preserved so existing callers
    /// (tests, simple hosts) keep compiling. Equivalent to passing
    /// <see cref="SearchOptions.Default"/>.</summary>
    public static List<SearchMatch> Scan(
        TerminalCell[][] rows, string needle, CancellationToken ct)
        => Scan(rows, needle, SearchOptions.Default, ct);

    private static void FindInRow(TerminalCell[] row, int absRow,
        string needle, SearchOptions options, Regex? rx, List<SearchMatch> into)
    {
        // Build a searchable haystack. Astral-plane runes (most emoji,
        // CJK Ext B+) encode as a surrogate pair — two chars in the
        // haystack but one cell — so we keep a parallel column map to
        // translate match offsets back to cell coordinates.
        //
        // colMap is rented from the shared pool: Scan runs row-by-row
        // on a single thread, so one rent/return per row avoids per-
        // search GC pressure (a 5000-row scrollback at 80 cols was
        // ~3.2 MB of int[] per search before this change).
        var sb = new StringBuilder(row.Length);
        int[] colMap = ArrayPool<int>.Shared.Rent(row.Length * 2);
        try
        {
            int mapLen = 0;
            for (int i = 0; i < row.Length; i++)
            {
                int rune = row[i].Rune;
                if (rune == 0)
                {
                    sb.Append(' ');
                    colMap[mapLen++] = i;
                }
                else if (rune <= 0xFFFF)
                {
                    sb.Append((char)rune);
                    colMap[mapLen++] = i;
                }
                else
                {
                    sb.Append(char.ConvertFromUtf32(rune));
                    colMap[mapLen++] = i;
                    colMap[mapLen++] = i;
                }
            }
            var haystack = sb.ToString();
            if (rx != null)
            {
                foreach (Match m in rx.Matches(haystack))
                {
                    if (m.Length == 0) continue;
                    if (options.WholeWord && !IsWholeWord(haystack, m.Index, m.Length)) continue;
                    int startCell = colMap[m.Index];
                    int endCell   = colMap[m.Index + m.Length - 1];
                    into.Add(new SearchMatch(absRow, startCell, endCell - startCell + 1));
                }
            }
            else
            {
                var cmp = options.CaseSensitive
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;
                int from = 0;
                while (from <= haystack.Length - needle.Length)
                {
                    int idx = haystack.IndexOf(needle, from, cmp);
                    if (idx < 0) break;
                    if (!options.WholeWord || IsWholeWord(haystack, idx, needle.Length))
                    {
                        int startCell = colMap[idx];
                        int endCell   = colMap[idx + needle.Length - 1];
                        into.Add(new SearchMatch(absRow, startCell, endCell - startCell + 1));
                    }
                    from = idx + Math.Max(1, needle.Length);
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(colMap);
        }
    }

    private static bool IsWholeWord(string haystack, int idx, int length)
    {
        bool leftOk  = idx == 0                     || !IsWordChar(haystack[idx - 1]);
        bool rightOk = idx + length >= haystack.Length || !IsWordChar(haystack[idx + length]);
        return leftOk && rightOk;
    }

    private static bool IsWordChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_';
}
