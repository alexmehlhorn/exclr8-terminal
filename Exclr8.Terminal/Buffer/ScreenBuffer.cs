using System;
using System.Collections.Generic;

namespace Exclr8.Terminal.Buffer;

/// <summary>
/// One of the two screen buffers (primary or alternate). Owns the
/// visible row array, scrollback ring, and all scroll primitives.
///
/// <para>Scroll-region variants restrict movement to a [top, bottom]
/// range (0-indexed, inclusive). Rows outside the region never move.
/// Content scrolled off the top of a non-full-screen region is
/// discarded (matches xterm behaviour) — scrollback only receives
/// rows that came off the top of the full screen.</para>
/// </summary>
public sealed class ScreenBuffer
{
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int ScrollbackLimit { get; set; }

    public LinkedList<TerminalCell[]> Scrollback { get; } = new();
    private readonly List<TerminalCell[]> _rows = new();

    public ScreenBuffer(int cols, int rows, int scrollbackLimit)
    {
        Cols = cols;
        Rows = rows;
        ScrollbackLimit = scrollbackLimit;
        for (int i = 0; i < rows; i++) _rows.Add(new TerminalCell[cols]);
    }

    public TerminalCell[] GetRow(int r) => _rows[r];

    public void Resize(int cols, int rows)
    {
        if (cols != Cols)
        {
            for (int r = 0; r < _rows.Count; r++)
            {
                var old = _rows[r];
                var next = new TerminalCell[cols];
                Array.Copy(old, next, Math.Min(old.Length, cols));
                _rows[r] = next;
            }
            var node = Scrollback.First;
            while (node != null)
            {
                var old = node.Value;
                var next = new TerminalCell[cols];
                Array.Copy(old, next, Math.Min(old.Length, cols));
                node.Value = next;
                node = node.Next;
            }
            Cols = cols;
        }

        if (rows > Rows)
        {
            for (int i = Rows; i < rows; i++) _rows.Add(new TerminalCell[Cols]);
        }
        else if (rows < Rows)
        {
            // Shrink: drop the top rows on the floor. We deliberately do
            // NOT push them to scrollback — repeated grow/shrink cycles
            // (e.g. moving a cell between layouts with different cell
            // sizes) would otherwise add the same content to scrollback
            // on every shrink, creating duplicate history. Scrollback
            // should grow from live shell output scrolling off the top,
            // not from layout-driven resizes. (A full fix would reflow
            // lines to the new width; tracked separately.)
            int extra = Rows - rows;
            for (int i = 0; i < extra; i++) _rows.RemoveAt(0);
        }
        Rows = rows;
    }

    // ---- Full-screen primitives (used when region == whole screen) ----

    /// <summary>Scroll the whole screen up by 1. Topmost row goes to
    /// scrollback; a blank row is appended at the bottom.</summary>
    public void ScrollUp()
    {
        PushScrollback(_rows[0]);
        _rows.RemoveAt(0);
        _rows.Add(new TerminalCell[Cols]);
    }

    /// <summary>Scroll the whole screen down by 1. Blank row inserted
    /// at the top; bottom row dropped (not scrollback — this is an RI /
    /// DECSET 6 behaviour, not an output scroll).</summary>
    public void ScrollDown()
    {
        _rows.Insert(0, new TerminalCell[Cols]);
        _rows.RemoveAt(_rows.Count - 1);
    }

    // ---- Region-aware scroll operations ----

    /// <summary>
    /// Scroll region [top,bottom] up by n. Lines evicted from the top
    /// of the region go to scrollback ONLY when the region covers the
    /// full screen — xterm's documented behaviour. Otherwise discarded.
    /// </summary>
    public void ScrollUpRegion(int top, int bottom, int n)
    {
        top    = Math.Max(0, top);
        bottom = Math.Min(Rows - 1, bottom);
        if (top > bottom || n <= 0) return;
        bool fullScreen = top == 0 && bottom == Rows - 1;
        n = Math.Min(n, bottom - top + 1);
        for (int i = 0; i < n; i++)
        {
            var evicted = _rows[top];
            if (fullScreen) PushScrollback(evicted);
            _rows.RemoveAt(top);
            _rows.Insert(bottom, new TerminalCell[Cols]);
        }
    }

    /// <summary>
    /// Scroll region [top,bottom] down by n. Lines pushed off the
    /// bottom of the region are discarded (not scrollback — this is a
    /// reverse-index, not output).
    /// </summary>
    public void ScrollDownRegion(int top, int bottom, int n)
    {
        top    = Math.Max(0, top);
        bottom = Math.Min(Rows - 1, bottom);
        if (top > bottom || n <= 0) return;
        n = Math.Min(n, bottom - top + 1);
        for (int i = 0; i < n; i++)
        {
            _rows.RemoveAt(bottom);
            _rows.Insert(top, new TerminalCell[Cols]);
        }
    }

    // ---- IL / DL — insert/delete lines respecting the scroll bottom ----

    /// <summary>Insert <paramref name="n"/> blank lines at
    /// <paramref name="at"/>, pushing content down. Lines pushed past
    /// <paramref name="scrollBottom"/> are discarded.</summary>
    public void InsertLines(int at, int n, int scrollBottom)
    {
        if (at < 0 || at >= Rows) return;
        scrollBottom = Math.Min(scrollBottom, Rows - 1);
        if (at > scrollBottom) return;
        n = Math.Min(n, scrollBottom - at + 1);
        for (int i = 0; i < n; i++)
        {
            _rows.RemoveAt(scrollBottom);
            _rows.Insert(at, new TerminalCell[Cols]);
        }
    }

    /// <summary>Delete <paramref name="n"/> lines at
    /// <paramref name="at"/>, pulling content up. Blanks fill from
    /// <paramref name="scrollBottom"/> downward.</summary>
    public void DeleteLines(int at, int n, int scrollBottom)
    {
        if (at < 0 || at >= Rows) return;
        scrollBottom = Math.Min(scrollBottom, Rows - 1);
        if (at > scrollBottom) return;
        n = Math.Min(n, scrollBottom - at + 1);
        for (int i = 0; i < n; i++)
        {
            _rows.RemoveAt(at);
            _rows.Insert(scrollBottom, new TerminalCell[Cols]);
        }
    }

    // Legacy overloads (full screen) for existing callers.
    public void InsertLines(int at, int n) => InsertLines(at, n, Rows - 1);
    public void DeleteLines(int at, int n) => DeleteLines(at, n, Rows - 1);

    public void Clear()
    {
        foreach (var row in _rows) Array.Clear(row, 0, row.Length);
    }

    public void ClearScrollback() => Scrollback.Clear();

    private void PushScrollback(TerminalCell[] row)
    {
        if (ScrollbackLimit <= 0) return;
        // Skip fully-blank rows: on initial layout the buffer starts at
        // its default 24 rows and then shrinks to whatever the cell
        // height accommodates. The top rows evicted by that shrink are
        // always empty (no output yet) and shouldn't count as
        // scrollback the user can navigate into — it'd give them a
        // phantom screen of nothing above the first prompt.
        if (IsBlankRow(row)) return;
        Scrollback.AddLast(row);
        while (Scrollback.Count > ScrollbackLimit) Scrollback.RemoveFirst();
    }

    private static bool IsBlankRow(TerminalCell[] row)
    {
        for (int i = 0; i < row.Length; i++)
            if (row[i].Rune != 0) return false;
        return true;
    }
}
