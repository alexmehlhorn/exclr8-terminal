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
///
/// <para><b>Row storage:</b> <see cref="_rows"/> is a physical list
/// plus a logical head index <see cref="_rowsHead"/>. Logical row 0
/// maps to physical <c>_rows[_rowsHead]</c>; logical row r maps to
/// <c>_rows[(_rowsHead + r) % _rows.Count]</c>. A full-screen scroll
/// just bumps the head, which makes the scroll O(1) instead of the
/// O(Rows) that <c>List.RemoveAt(0) + Insert(end)</c> paid. Partial-
/// region scrolls still shift pointers within the region (there's no
/// cheap trick for those) but go through the same logical→physical
/// mapping. <see cref="Resize"/> normalises head to 0 before mutating
/// the physical list so list growth / shrink stays simple.</para>
/// </summary>
public sealed class ScreenBuffer
{
    public int Cols { get; private set; }
    public int Rows { get; private set; }

    private int _scrollbackLimit;
    public int ScrollbackLimit
    {
        get => _scrollbackLimit;
        set
        {
            _scrollbackLimit = value;
            if (value > 0) Scrollback.Capacity = value;
            else           Scrollback.Clear();
        }
    }

    public ScrollbackRing Scrollback { get; }
    private readonly List<TerminalCell[]> _rows = new();
    private int _rowsHead;

    public ScreenBuffer(int cols, int rows, int scrollbackLimit)
    {
        Cols = cols;
        Rows = rows;
        _scrollbackLimit = scrollbackLimit;
        Scrollback = new ScrollbackRing(Math.Max(1, scrollbackLimit));
        for (int i = 0; i < rows; i++) _rows.Add(new TerminalCell[cols]);
    }

    /// <summary>Physical index into <see cref="_rows"/> for logical
    /// row <paramref name="r"/>.</summary>
    private int Physical(int r) => (_rowsHead + r) % _rows.Count;

    public TerminalCell[] GetRow(int r) => _rows[Physical(r)];

    public void Resize(int cols, int rows)
    {
        // Normalise the physical list to head = 0 before any growth /
        // shrink — additions go to the end of the list and shrinkage
        // drops from the top, both of which assume logical row r is
        // physical index r.
        NormaliseHead();

        if (cols != Cols)
        {
            for (int r = 0; r < _rows.Count; r++)
            {
                var old = _rows[r];
                var next = new TerminalCell[cols];
                Array.Copy(old, next, Math.Min(old.Length, cols));
                _rows[r] = next;
            }
            for (int i = 0; i < Scrollback.Count; i++)
            {
                var old = Scrollback[i];
                var next = new TerminalCell[cols];
                Array.Copy(old, next, Math.Min(old.Length, cols));
                Scrollback[i] = next;
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

    private void NormaliseHead()
    {
        if (_rowsHead == 0 || _rows.Count == 0) return;
        int n = _rows.Count;
        var ordered = new TerminalCell[n][];
        for (int i = 0; i < n; i++) ordered[i] = _rows[(_rowsHead + i) % n];
        _rows.Clear();
        _rows.AddRange(ordered);
        _rowsHead = 0;
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

        if (fullScreen)
        {
            // O(1) rotate: physical slot at _rowsHead currently holds
            // logical row 0. Push it to scrollback, drop a blank in
            // its place (reusing the scrollback evictee when the ring
            // is saturated), then advance the head so that slot
            // becomes the new logical bottom.
            for (int i = 0; i < n; i++)
            {
                int headIdx = _rowsHead;
                var evicted = _rows[headIdx];
                var recycled = PushScrollback(evicted);
                _rows[headIdx] = TakeOrAllocBlank(recycled);
                _rowsHead = (_rowsHead + 1) % _rows.Count;
            }
            return;
        }

        // Partial region: shift row pointers within [top, bottom]. N
        // pointer moves per scrolled line — same as before the circular
        // conversion. The evicted top row's array is recycled as the
        // new bottom blank.
        for (int i = 0; i < n; i++)
        {
            var evicted = _rows[Physical(top)];
            for (int r = top; r < bottom; r++)
                _rows[Physical(r)] = _rows[Physical(r + 1)];
            Array.Clear(evicted, 0, Cols);
            _rows[Physical(bottom)] = evicted;
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
        bool fullScreen = top == 0 && bottom == Rows - 1;
        n = Math.Min(n, bottom - top + 1);

        if (fullScreen)
        {
            // O(1) reverse-rotate: decrement head first, then clear
            // the row at the new head position — it used to be the
            // logical bottom, now it becomes logical row 0 (blank).
            for (int i = 0; i < n; i++)
            {
                _rowsHead = (_rowsHead - 1 + _rows.Count) % _rows.Count;
                Array.Clear(_rows[_rowsHead], 0, Cols);
            }
            return;
        }

        // Partial region: shift pointers within [top, bottom].
        for (int i = 0; i < n; i++)
        {
            var evicted = _rows[Physical(bottom)];
            for (int r = bottom; r > top; r--)
                _rows[Physical(r)] = _rows[Physical(r - 1)];
            Array.Clear(evicted, 0, Cols);
            _rows[Physical(top)] = evicted;
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
            var evicted = _rows[Physical(scrollBottom)];
            for (int r = scrollBottom; r > at; r--)
                _rows[Physical(r)] = _rows[Physical(r - 1)];
            Array.Clear(evicted, 0, Cols);
            _rows[Physical(at)] = evicted;
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
            var evicted = _rows[Physical(at)];
            for (int r = at; r < scrollBottom; r++)
                _rows[Physical(r)] = _rows[Physical(r + 1)];
            Array.Clear(evicted, 0, Cols);
            _rows[Physical(scrollBottom)] = evicted;
        }
    }

    public void Clear()
    {
        foreach (var row in _rows) Array.Clear(row, 0, row.Length);
    }

    public void ClearScrollback() => Scrollback.Clear();

    /// <summary>Push a row into scrollback. Returns the array that was
    /// evicted from the ring (if the ring was at capacity) so callers
    /// can reuse it as the new blank row, skipping an allocation on
    /// steady-state scroll.</summary>
    private TerminalCell[]? PushScrollback(TerminalCell[] row)
    {
        if (ScrollbackLimit <= 0) return null;
        // Skip fully-blank rows: on initial layout the buffer starts at
        // its default 24 rows and then shrinks to whatever the cell
        // height accommodates. The top rows evicted by that shrink are
        // always empty (no output yet) and shouldn't count as
        // scrollback the user can navigate into — it'd give them a
        // phantom screen of nothing above the first prompt.
        if (IsBlankRow(row)) return null;
        if (Scrollback.Capacity != ScrollbackLimit) Scrollback.Capacity = ScrollbackLimit;
        return Scrollback.Add(row);
    }

    /// <summary>Return either <paramref name="recycled"/> (cleared in
    /// place) or a freshly-allocated blank row of the current width.
    /// Used anywhere we need a blank-row slot after a region scroll.</summary>
    private TerminalCell[] TakeOrAllocBlank(TerminalCell[]? recycled)
    {
        if (recycled != null && recycled.Length == Cols)
        {
            Array.Clear(recycled, 0, Cols);
            return recycled;
        }
        return new TerminalCell[Cols];
    }

    private static bool IsBlankRow(TerminalCell[] row)
    {
        for (int i = 0; i < row.Length; i++)
            if (row[i].Rune != 0) return false;
        return true;
    }
}
