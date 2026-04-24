using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Exclr8.Terminal.Buffer;

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
    private readonly List<SearchMatch> _matches = new();

    public string? Needle { get; private set; }
    public IReadOnlyList<SearchMatch> Matches => _matches;
    public int CurrentIndex { get; private set; } = -1;

    /// <summary>Replace match state atomically. <paramref name="viewBottomAbs"/>
    /// is used to pick the match closest to the current viewport so
    /// "next" naturally moves forward from where the user is looking.</summary>
    public void Set(string? needle, List<SearchMatch> matches, int viewBottomAbs)
    {
        Needle = string.IsNullOrEmpty(needle) ? null : needle;
        _matches.Clear();
        _matches.AddRange(matches);
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

    /// <summary>Scan a row snapshot for case-insensitive occurrences of
    /// <paramref name="needle"/>. Checks <paramref name="ct"/> between
    /// rows so a superseded search returns quickly. Safe to run
    /// off-thread against a snapshot captured on the UI thread.</summary>
    public static List<SearchMatch> Scan(
        TerminalCell[][] rows, string needle, CancellationToken ct)
    {
        var matches = new List<SearchMatch>();
        for (int r = 0; r < rows.Length; r++)
        {
            ct.ThrowIfCancellationRequested();
            var row = rows[r];
            if (row != null) FindInRow(row, r, needle, matches);
        }
        return matches;
    }

    private static void FindInRow(TerminalCell[] row, int absRow,
        string needle, List<SearchMatch> into)
    {
        // Build a searchable haystack. Astral-plane runes (most emoji,
        // CJK Ext B+) encode as a surrogate pair — two chars in the
        // haystack but one cell — so we keep a parallel column map to
        // translate match offsets back to cell coordinates.
        var sb = new StringBuilder(row.Length);
        var colMap = new int[row.Length * 2];
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
        int from = 0;
        while (from <= haystack.Length - needle.Length)
        {
            int idx = haystack.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;
            int startCell = colMap[idx];
            int endCell   = colMap[idx + needle.Length - 1];
            into.Add(new SearchMatch(absRow, startCell, endCell - startCell + 1));
            from = idx + Math.Max(1, needle.Length);
        }
    }
}
