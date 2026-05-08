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
    /// <summary>Per-row wrap flag. <c>_rowsWrapped[Physical(r)]</c> is
    /// true when row r is the continuation of an auto-wrap from r-1
    /// (or, for r=0, from the bottom of scrollback). Set by the buffer
    /// when DECAWM kicks in; consumed by reflow on resize.</summary>
    private readonly List<bool> _rowsWrapped = new();
    private int _rowsHead;

    public bool GetWrapped(int r) => _rowsWrapped[Physical(r)];
    public void SetWrapped(int r, bool v) => _rowsWrapped[Physical(r)] = v;

    public ScreenBuffer(int cols, int rows, int scrollbackLimit)
    {
        Cols = cols;
        Rows = rows;
        _scrollbackLimit = scrollbackLimit;
        Scrollback = new ScrollbackRing(Math.Max(1, scrollbackLimit));
        for (int i = 0; i < rows; i++)
        {
            _rows.Add(new TerminalCell[cols]);
            _rowsWrapped.Add(false);
        }
    }

    /// <summary>Physical index into <see cref="_rows"/> for logical
    /// row <paramref name="r"/>.</summary>
    private int Physical(int r) => (_rowsHead + r) % _rows.Count;

    public TerminalCell[] GetRow(int r) => _rows[Physical(r)];

    /// <summary>Resize the screen + scrollback to a new column / row
    /// count. When <paramref name="cols"/> changes, lines previously
    /// marked as wrapped continuations are reflowed to the new width
    /// — see <see cref="Reflow"/>. Returns the new (row, col) cursor
    /// position derived from <paramref name="cursorRow"/> /
    /// <paramref name="cursorCol"/>; the caller updates the buffer's
    /// cursor state from the result.</summary>
    public (int row, int col) Resize(int cols, int rows, int cursorRow, int cursorCol)
    {
        // Normalise the physical list to head = 0 before any growth /
        // shrink — additions go to the end of the list and shrinkage
        // drops from the top, both of which assume logical row r is
        // physical index r.
        NormaliseHead();

        if (cols != Cols)
        {
            // Pass the TARGET row count so Reflow places exactly
            // `rows` entries in _rows and routes the rest into
            // scrollback. Without this, Reflow would size to the
            // OLD Rows count and the post-Reflow rows-shrink below
            // would have to drop already-placed content from the top
            // — silently losing genuine scrolled-out history.
            (cursorRow, cursorCol) = Reflow(cols, rows, cursorRow, cursorCol);
            Cols = cols;
        }

        // Use _rows.Count, not the Rows field — when Reflow ran above
        // it sized _rows to match the target `rows` exactly, so the
        // grow/shrink branches here are typically no-ops on a
        // cols-changed resize. The branches matter for rows-only
        // resize (no Reflow); they also handle the case where Reflow
        // produced fewer redistributed rows than the target.
        if (rows > _rows.Count)
        {
            // Grow: pad with blank rows at the bottom. Always safe —
            // we're never losing content.
            while (_rows.Count < rows)
            {
                _rows.Add(new TerminalCell[Cols]);
                _rowsWrapped.Add(false);
            }
        }
        else if (rows < _rows.Count)
        {
            // Shrink. Strategy:
            //   1. Drop blank tail rows (free).
            //   2. Drop blank top rows (also free, decrementing
            //      cursor since content shifts up).
            //   3. If we still need to shrink and the cursor is
            //      below the new bottom, evict top rows. On the
            //      primary screen they go to scrollback (real
            //      shell history shouldn't vanish on resize); on
            //      alt-screen they're dropped (alt has no
            //      scrollback by design and TUIs redraw on
            //      SIGWINCH).
            //   4. Otherwise drop from the bottom.
            int extra = _rows.Count - rows;
            while (extra > 0 && _rows.Count > 0 && IsBlankRow(_rows[^1]))
            {
                _rows.RemoveAt(_rows.Count - 1);
                _rowsWrapped.RemoveAt(_rowsWrapped.Count - 1);
                extra--;
            }
            while (extra > 0 && _rows.Count > 0 && IsBlankRow(_rows[0]))
            {
                _rows.RemoveAt(0);
                _rowsWrapped.RemoveAt(0);
                extra--;
                cursorRow--;
            }
            if (extra > 0 && cursorRow >= rows)
            {
                int dropTop = Math.Min(extra, cursorRow - rows + 1);
                for (int i = 0; i < dropTop; i++)
                {
                    if (ScrollbackLimit > 0)
                    {
                        // Push the top row into scrollback so genuine
                        // shell history isn't silently lost on resize.
                        Scrollback.Add(_rows[0], _rowsWrapped[0]);
                    }
                    _rows.RemoveAt(0);
                    _rowsWrapped.RemoveAt(0);
                }
                cursorRow -= dropTop;
                extra -= dropTop;
            }
            while (extra > 0 && _rows.Count > 0)
            {
                _rows.RemoveAt(_rows.Count - 1);
                _rowsWrapped.RemoveAt(_rowsWrapped.Count - 1);
                extra--;
            }
            cursorRow = Math.Max(0, Math.Min(cursorRow, rows - 1));
        }
        Rows = rows;
        cursorRow = Math.Max(0, Math.Min(cursorRow, Rows - 1));
        cursorCol = Math.Max(0, Math.Min(cursorCol, Cols - 1));
        return (cursorRow, cursorCol);
    }

    /// <summary>Reflow scrollback + live screen to a new column count.
    /// Wrapped row groups are joined into a single logical line and
    /// re-split at the new width. Wide cells never straddle the new
    /// wrap boundary — a blank cell is left at the row's end if a
    /// wide cell would otherwise span. Returns the cursor position in
    /// the reflowed live screen.</summary>
    private (int row, int col) Reflow(int newCols, int newRows, int cursorRow, int cursorCol)
    {
        // Collect ALL existing rows (scrollback + live) as logical lines
        // in order. Each line is a list of cells; the boundary between
        // lines is where the source row's wrap flag is false.
        var lines = new List<List<TerminalCell>>();
        // Cursor tracking: which logical line the cursor is in
        // (cursorLineIdx) and the cell offset within that line
        // (cursorOffsetInLine). Carried into the redistribution loop
        // so we can land the cursor on the EXACT cell — including on
        // blank logical lines, where a global cell-index would clash
        // with the end of the previous line.
        int cursorLineIdx = -1;
        int cursorOffsetInLine = 0;
        // Scrollback rows first.
        var srcCols = Cols;
        var current = new List<TerminalCell>();
        for (int i = 0; i < Scrollback.Count; i++)
        {
            var row = Scrollback[i];
            int len = TrimmedLength(row, srcCols);
            for (int c = 0; c < len; c++) current.Add(row[c]);
            // Determine whether the next physical row in *logical* order
            // is a continuation: that's either the next scrollback row
            // (if any) or the first live-screen row (when this is the
            // last scrollback row). Without the live-row fallback the
            // last scrollback row prematurely closes a logical line
            // that actually continues into the live screen, leaving
            // the first N narrow rows un-rejoined on widen.
            bool nextIsWrapped;
            if (i + 1 < Scrollback.Count)
                nextIsWrapped = Scrollback.IsWrapped(i + 1);
            else
                nextIsWrapped = _rows.Count > 0 && _rowsWrapped[Physical(0)];
            if (!nextIsWrapped)
            {
                lines.Add(current);
                current = new List<TerminalCell>();
            }
        }

        // Trailing blank live rows below the cursor are padding, not
        // content. Find the last live row that's worth gathering: the
        // max of the cursor row and the last non-blank row. Beyond
        // that, rows are filled with blanks at the end of the new
        // layout instead of contributing empty logical lines.
        int lastInteresting = cursorRow;
        for (int r = _rows.Count - 1; r > lastInteresting; r--)
        {
            if (TrimmedLength(_rows[Physical(r)], srcCols) > 0
                || _rowsWrapped[Physical(r)])
            {
                lastInteresting = r;
                break;
            }
        }

        for (int r = 0; r <= lastInteresting && r < _rows.Count; r++)
        {
            var row = _rows[Physical(r)];
            int len = TrimmedLength(row, srcCols);
            // Capture cursor (line index, offset) before adding this
            // row's cells. The line we end up in is whichever line
            // gets pushed (or whichever empty line is added) for this
            // physical row.
            if (r == cursorRow)
            {
                cursorLineIdx = lines.Count;
                cursorOffsetInLine = current.Count + Math.Min(cursorCol, srcCols);
            }
            for (int c = 0; c < len; c++) current.Add(row[c]);
            bool nextWrapped = r + 1 < _rows.Count && _rowsWrapped[Physical(r + 1)];
            if (!nextWrapped)
            {
                lines.Add(current);
                current = new List<TerminalCell>();
            }
        }
        if (current.Count > 0)
        {
            lines.Add(current);
        }

        // Redistribute. Each logical line splits into ceil(len /
        // newCols) physical rows. Wide cells get pushed to the next
        // row if they would span a boundary. Empty logical lines map
        // to one blank row.
        var redistributed = new List<(TerminalCell[] Row, bool Wrapped)>();
        int newCursorRow = -1, newCursorCol = -1;
        for (int lineIdx = 0; lineIdx < lines.Count; lineIdx++)
        {
            var line = lines[lineIdx];
            bool isCursorLine = lineIdx == cursorLineIdx;
            // Empty logical lines still occupy one redistributed row,
            // and the cursor can legitimately be on one (a blank row
            // between paragraphs). Place the cursor explicitly on
            // that row before adding it.
            if (line.Count == 0)
            {
                if (isCursorLine && newCursorRow < 0)
                {
                    newCursorRow = redistributed.Count;
                    newCursorCol = 0;
                }
                redistributed.Add((new TerminalCell[newCols], false));
                continue;
            }
            int idx = 0;
            bool wrappedFlag = false;
            int firstRowOfLine = redistributed.Count;
            while (idx < line.Count)
            {
                var rowArr = new TerminalCell[newCols];
                int outCol = 0;
                int curRowIndex = redistributed.Count;
                while (outCol < newCols && idx < line.Count)
                {
                    // Capture the cursor BEFORE writing the cell at
                    // its source-line index — that's the mapping the
                    // host wants ("the cursor is on this cell"). Done
                    // via the explicit (line, offset) pair so wide-
                    // cell wrap-skips don't throw off the math.
                    if (isCursorLine && newCursorRow < 0 && idx == cursorOffsetInLine)
                    {
                        newCursorRow = curRowIndex;
                        newCursorCol = outCol;
                    }
                    var cell = line[idx];
                    bool isWide = (cell.Flags2 & CellFlags2.IsWide) != 0;
                    if (isWide && outCol + 1 >= newCols)
                    {
                        // No room for the wide pair on this line — wrap.
                        // Leave a trailing blank in outCol so the wide
                        // cell starts the next row.
                        break;
                    }
                    rowArr[outCol++] = cell;
                    idx++;
                    if (isWide && idx < line.Count
                        && (line[idx].Flags2 & CellFlags2.IsContinuation) != 0)
                    {
                        rowArr[outCol++] = line[idx];
                        idx++;
                    }
                }
                redistributed.Add((rowArr, wrappedFlag));
                wrappedFlag = true; // subsequent rows of this line are continuations
            }
            // Cursor at end-of-line (offset == line.Count) lands one
            // column past the last placed cell. Walk to where the
            // last cell of the line landed and step one to the right
            // on the same row, wrapping to next row if at margin.
            if (isCursorLine && newCursorRow < 0
                && cursorOffsetInLine >= line.Count)
            {
                int lastRowIndex = redistributed.Count - 1;
                // Find the trailing-cell column on the last row by
                // counting non-default cells from the right. Cheap —
                // newCols is small and we only do this once per
                // line at most.
                var lastRow = redistributed[lastRowIndex].Row;
                int lastCol = newCols - 1;
                while (lastCol > 0 && lastRow[lastCol].Rune == 0
                       && (lastRow[lastCol].Flags2 & CellFlags2.IsContinuation) == 0)
                    lastCol--;
                int placeCol = lastCol + 1;
                if (placeCol >= newCols)
                {
                    // Past the right edge — deferred-wrap convention.
                    newCursorRow = lastRowIndex;
                    newCursorCol = newCols - 1;
                }
                else
                {
                    newCursorRow = lastRowIndex;
                    newCursorCol = placeCol;
                }
            }
        }
        if (newCursorRow < 0)
        {
            // Cursor was past all content — pin to the end.
            newCursorRow = redistributed.Count;
            newCursorCol = 0;
        }

        // Split into scrollback (front) and live screen (last
        // `newRows`). Using the TARGET row count (not the buffer's
        // current Rows) means the live screen ends up sized for the
        // post-resize layout, and any overflow is routed into
        // scrollback right here — instead of being placed on the
        // live screen and then dropped by the rows-shrink step,
        // which would lose genuine history.
        //
        // The alternate screen is configured with ScrollbackLimit =
        // 0 so its history is intentionally transient — reflow must
        // honour that. Without the gate, reflow that produced more
        // rows than the live screen could hold would quietly leak
        // them into the alt screen's ring (capacity is clamped to a
        // minimum of 1 internally), and a later switch back to the
        // primary would expose ghost rows.
        int liveCount = newRows;
        int sbCount = ScrollbackLimit > 0
            ? Math.Max(0, redistributed.Count - liveCount)
            : 0;
        // Replace scrollback contents.
        Scrollback.Clear();
        for (int i = 0; i < sbCount; i++)
        {
            Scrollback.Add(redistributed[i].Row, redistributed[i].Wrapped);
        }
        // Replace live screen.
        _rows.Clear();
        _rowsWrapped.Clear();
        for (int i = sbCount; i < sbCount + liveCount; i++)
        {
            if (i < redistributed.Count)
            {
                _rows.Add(redistributed[i].Row);
                _rowsWrapped.Add(redistributed[i].Wrapped);
            }
            else
            {
                _rows.Add(new TerminalCell[newCols]);
                _rowsWrapped.Add(false);
            }
        }
        _rowsHead = 0;

        // Re-derive cursor in live-screen coordinates.
        // Clamp against the new row count, not the old Rows — Reflow
        // sized the live screen for newRows, so cursor positions
        // beyond it are post-reflow invalid.
        int outCursorRow = Math.Max(0, Math.Min(newCursorRow - sbCount, newRows - 1));
        int outCursorCol = Math.Max(0, Math.Min(newCursorCol, newCols - 1));
        return (outCursorRow, outCursorCol);
    }

    /// <summary>Length of meaningful content in <paramref name="row"/>:
    /// strip trailing cells that are visually blank (rune 0, no SGR
    /// state). Required for sane reflow — a row that printed "hi" in
    /// 80 cols shouldn't gain 78 trailing blanks when reflowed to 40
    /// cols, blowing into a second row.</summary>
    private static int TrimmedLength(TerminalCell[] row, int upTo)
    {
        int n = Math.Min(row.Length, upTo);
        while (n > 0)
        {
            var cell = row[n - 1];
            // Real glyph (anything other than blank or literal space)
            // anchors the row's length. Spaces alone are treated as
            // padding — programs that BCE-erase to end of line emit
            // either rune=0 or a literal space and rely on the bg
            // attribute to colour the gap. For reflow we don't want
            // those filler cells to make the logical line "wide
            // enough" to occupy multiple rows when re-broken at a
            // narrower width — that turns a single coloured line
            // into N rows of trailing bg-only filler.
            if (cell.Rune != 0 && cell.Rune != 0x20) break;
            // Anything beyond a pure bg colour does anchor: foreground
            // colour, underline / bold / italic flags, hyperlink runs.
            // Only a cell that's "blank with at most a background"
            // counts as trimmable BCE padding.
            if (cell.Flags != 0 || cell.Flags2 != 0) break;
            if (cell.FgIndex != 0 || cell.FgRgb != 0) break;
            if (cell.HyperlinkId != 0) break;
            n--;
        }
        return n;
    }

    private void NormaliseHead()
    {
        if (_rowsHead == 0 || _rows.Count == 0) return;
        int n = _rows.Count;
        var ordered = new TerminalCell[n][];
        var orderedW = new bool[n];
        for (int i = 0; i < n; i++)
        {
            ordered[i]  = _rows[(_rowsHead + i) % n];
            orderedW[i] = _rowsWrapped[(_rowsHead + i) % n];
        }
        _rows.Clear();
        _rows.AddRange(ordered);
        _rowsWrapped.Clear();
        for (int i = 0; i < n; i++) _rowsWrapped.Add(orderedW[i]);
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
                bool evictedWrapped = _rowsWrapped[headIdx];
                var recycled = PushScrollback(evicted, evictedWrapped);
                _rows[headIdx] = TakeOrAllocBlank(recycled);
                _rowsWrapped[headIdx] = false; // new blank bottom row
                _rowsHead = (_rowsHead + 1) % _rows.Count;
            }
            return;
        }

        // Partial region: shift row pointers within [top, bottom]. N
        // pointer moves per scrolled line — same as before the circular
        // conversion. The evicted top row's array is recycled as the
        // new bottom blank. Wrap flags shift in lockstep with rows.
        for (int i = 0; i < n; i++)
        {
            var evicted = _rows[Physical(top)];
            for (int r = top; r < bottom; r++)
            {
                _rows[Physical(r)]        = _rows[Physical(r + 1)];
                _rowsWrapped[Physical(r)] = _rowsWrapped[Physical(r + 1)];
            }
            Array.Clear(evicted, 0, Cols);
            _rows[Physical(bottom)]        = evicted;
            _rowsWrapped[Physical(bottom)] = false;
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
                _rowsWrapped[_rowsHead] = false;
            }
            return;
        }

        // Partial region: shift pointers within [top, bottom].
        for (int i = 0; i < n; i++)
        {
            var evicted = _rows[Physical(bottom)];
            for (int r = bottom; r > top; r--)
            {
                _rows[Physical(r)]        = _rows[Physical(r - 1)];
                _rowsWrapped[Physical(r)] = _rowsWrapped[Physical(r - 1)];
            }
            Array.Clear(evicted, 0, Cols);
            _rows[Physical(top)]        = evicted;
            _rowsWrapped[Physical(top)] = false;
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
            {
                _rows[Physical(r)]        = _rows[Physical(r - 1)];
                _rowsWrapped[Physical(r)] = _rowsWrapped[Physical(r - 1)];
            }
            Array.Clear(evicted, 0, Cols);
            _rows[Physical(at)]        = evicted;
            _rowsWrapped[Physical(at)] = false;
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
            {
                _rows[Physical(r)]        = _rows[Physical(r + 1)];
                _rowsWrapped[Physical(r)] = _rowsWrapped[Physical(r + 1)];
            }
            Array.Clear(evicted, 0, Cols);
            _rows[Physical(scrollBottom)]        = evicted;
            _rowsWrapped[Physical(scrollBottom)] = false;
        }
    }

    public void Clear()
    {
        foreach (var row in _rows) Array.Clear(row, 0, row.Length);
        for (int i = 0; i < _rowsWrapped.Count; i++) _rowsWrapped[i] = false;
    }

    public void ClearScrollback() => Scrollback.Clear();

    /// <summary>Push a row into scrollback. Returns the array that was
    /// evicted from the ring (if the ring was at capacity) so callers
    /// can reuse it as the new blank row, skipping an allocation on
    /// steady-state scroll.</summary>
    private TerminalCell[]? PushScrollback(TerminalCell[] row, bool wrapped = false)
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
        return Scrollback.Add(row, wrapped);
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

