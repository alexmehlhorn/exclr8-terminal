using System;
using System.Collections.Generic;
using System.Text;
using Exclr8.Terminal.Parser;
using Exclr8.Terminal.Render;

namespace Exclr8.Terminal.Buffer;

/// <summary>
/// Authoritative terminal state: cell grid, scrollback ring, cursor
/// position, SGR pen, DEC private-mode flags, active character set,
/// scrollback viewport offset, selection, OSC 8 hyperlink map, and a
/// DSR/DA reply queue. Driven by the <see cref="VtParser"/> via the
/// <see cref="IParserActions"/> surface.
/// </summary>
public sealed class TerminalBuffer : IParserActions
{
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int CursorRow { get; private set; }
    public int CursorCol { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public CursorStyle CursorStyle { get; private set; } = CursorStyle.BlockBlink;

    /// <summary>SGR pen applied to every <see cref="Print"/>. Read-only
    /// from outside; mutated internally by the SGR handlers.</summary>
    public TerminalCell PenTemplate => _pen;
    private TerminalCell _pen = TerminalCell.Blank;

    private readonly ScreenBuffer _primary;
    private readonly ScreenBuffer _alternate;
    private ScreenBuffer _active;

    public bool IsAltScreen => _active == _alternate;

    public int ScrollbackLimit
    {
        get => _primary.ScrollbackLimit;
        set { _primary.ScrollbackLimit = value; _alternate.ScrollbackLimit = 0; }
    }

    public int Revision { get; private set; }

    private readonly VtParser _parser;

    // Saved cursor state — primary and alternate screens each keep
    // their own snapshot so DECSC/DECRC while toggling alt screens
    // doesn't clobber the other screen's saved position.
    private readonly struct SavedCursor
    {
        public int Row { get; init; }
        public int Col { get; init; }
        public TerminalCell Pen { get; init; }
    }

    private SavedCursor _primarySaved;
    private SavedCursor _alternateSaved;

    private SavedCursor SnapshotCursor() =>
        new() { Row = CursorRow, Col = CursorCol, Pen = _pen };

    private void ApplyCursor(SavedCursor s)
    {
        CursorRow = Clamp(s.Row, 0, Rows - 1);
        CursorCol = Clamp(s.Col, 0, Cols - 1);
        _pen      = s.Pen;
    }

    // Scroll region (DECSTBM). 0-indexed, inclusive. Defaults to full screen.
    public int ScrollTop    { get; private set; }
    public int ScrollBottom { get; private set; }

    public enum Charset { Ascii, DecSpecialGraphics }
    private readonly Charset[] _gSlots = { Charset.Ascii, Charset.Ascii };
    private int _activeG; // 0 = G0, 1 = G1

    // DEC private modes.
    public bool BracketedPaste        { get; private set; }
    public bool ApplicationCursorKeys { get; private set; }
    public bool ApplicationKeypad     { get; private set; }
    public int  MouseMode             { get; private set; } // 0 / 1000 / 1002 / 1003
    public bool FocusEvents           { get; private set; }
    /// <summary>DECAWM — auto-wrap mode. On by default; when off, the
    /// cursor stays on the right margin and subsequent prints stomp
    /// the last cell instead of wrapping.</summary>
    public bool AutoWrap              { get; private set; } = true;
    /// <summary>DECOM — origin mode. When on, CUP/HVP row parameters
    /// are interpreted relative to the scroll region and the cursor
    /// is constrained within it.</summary>
    public bool OriginMode            { get; private set; }
    /// <summary>DECSCNM — reverse video. Flag for the renderer; the
    /// buffer itself does not swap pen colours.</summary>
    public bool ReverseVideo          { get; private set; }
    /// <summary>IRM — ANSI insert/replace mode (default replace).</summary>
    public bool InsertMode            { get; private set; }
    /// <summary>LNM — line feed/new line mode. When on, LF/VT/FF
    /// imply a carriage return as well.</summary>
    public bool LineFeedNewLine       { get; private set; }

    /// <summary>OSC 52 clipboard routing. Gated off by default because
    /// it lets remote processes silently scrape the host clipboard.
    /// The host opts in explicitly when it has user consent.</summary>
    public bool AllowClipboardAccess
    {
        get => _osc.AllowClipboardAccess;
        set => _osc.AllowClipboardAccess = value;
    }

    /// <summary>Default foreground reported to OSC 10 queries. Packed
    /// as 0xRRGGBB. Host layers that theme the terminal update this
    /// when the colour scheme changes.</summary>
    public uint DefaultForegroundRgb
    {
        get => _osc.DefaultForegroundRgb;
        set => _osc.DefaultForegroundRgb = value;
    }

    /// <summary>Default background for OSC 11 queries.</summary>
    public uint DefaultBackgroundRgb
    {
        get => _osc.DefaultBackgroundRgb;
        set => _osc.DefaultBackgroundRgb = value;
    }

    /// <summary>Cursor colour for OSC 12 queries.</summary>
    public uint DefaultCursorRgb
    {
        get => _osc.DefaultCursorRgb;
        set => _osc.DefaultCursorRgb = value;
    }

    // Scrollback viewport. 0 = at bottom; positive = scrolled up into
    // scrollback. TerminalControl resets this to 0 on any keystroke.
    // PixelScrollOffset carries the sub-line pixel remainder so the
    // renderer can slide content smoothly — wheel events accumulate in
    // pixel space and turn over into whole-line Offset bumps as they
    // cross a line height.
    private readonly ScrollViewport _viewport = new();
    public int ScrollOffset => _viewport.Offset;
    public double PixelScrollOffset => _viewport.PixelOffset;

    public TerminalSelection? Selection { get; private set; }

    // Search state lives in its own type (snapshot-on-UI / scan-off-thread
    // / apply-on-UI). Matches are in ABSOLUTE row coordinates (0 = oldest
    // scrollback row) so they stay glued to content as the user scrolls.
    private readonly SearchIndex _search = new();
    public string? SearchNeedle => _search.Needle;
    public IReadOnlyList<SearchMatch> SearchMatches => _search.Matches;
    public int CurrentMatchIndex => _search.CurrentIndex;

    // OSC handling (title, palette, hyperlinks, clipboard, queries).
    private readonly OscDispatcher _osc;

    // UTF-8 partial state for split chunks (unused now that the parser
    // handles it, but kept for future external Write(byte) callers).
    private readonly List<byte> _pendingReplies = new();

    // Last printable codepoint emitted — used by REP (CSI Ps b) to
    // repeat the preceding character. Reset to 0 on any control
    // sequence other than REP itself so REP after e.g. a newline is
    // a no-op, matching xterm.js's <c>precedingJoinState</c>.
    private int _lastPrintRune;

    // Custom tab stops. When null, defaults to every 8 cols.
    // HTS (ESC H) adds a stop, TBC (CSI g) clears.
    private bool[]? _tabStops;

    public byte[]? TakeReplies()
    {
        if (_pendingReplies.Count == 0) return null;
        var b = _pendingReplies.ToArray();
        _pendingReplies.Clear();
        return b;
    }

    /// <summary>Fired when an OSC 0 or OSC 2 sets the window title.</summary>
    public event EventHandler<string>? TitleChanged
    {
        add    => _osc.TitleChanged += value;
        remove => _osc.TitleChanged -= value;
    }

    /// <summary>Fired when OSC 0 or OSC 1 sets the icon name. Most
    /// shells emit OSC 0 which sets both title and icon name.</summary>
    public event EventHandler<string>? IconNameChanged
    {
        add    => _osc.IconNameChanged += value;
        remove => _osc.IconNameChanged -= value;
    }

    /// <summary>Fired when an OSC 52 ; c ; &lt;base64&gt; request
    /// arrives AND <see cref="AllowClipboardAccess"/> is true. The
    /// host decides whether to honour (copy to clipboard) or ignore.</summary>
    public event EventHandler<ClipboardRequestEventArgs>? ClipboardRequested
    {
        add    => _osc.ClipboardRequested += value;
        remove => _osc.ClipboardRequested -= value;
    }

    /// <summary>Host focus change. When DECSET 1004 (focus events) is
    /// enabled, we reply with ESC [ I (focus in) or ESC [ O (focus
    /// out). No-op otherwise.</summary>
    public void NotifyFocus(bool focused)
    {
        if (!FocusEvents) return;
        ReplyToPty(focused ? "\x1b[I"u8 : "\x1b[O"u8);
        Bump();
    }

    public TerminalBuffer(int cols, int rows)
    {
        cols = Math.Max(cols, 1);
        rows = Math.Max(rows, 1);
        Cols = cols; Rows = rows;
        _primary   = new ScreenBuffer(cols, rows, scrollbackLimit: 5000);
        _alternate = new ScreenBuffer(cols, rows, scrollbackLimit: 0);
        _active    = _primary;
        _parser    = new VtParser(this);
        _osc       = new OscDispatcher(bytes => ReplyToPty(bytes));
        ScrollBottom = rows - 1;
    }

    public TerminalCell[] GetVisibleRow(int r) => _active.GetRow(r);

    public int ScrollbackCount => _active.Scrollback.Count;

    /// <summary>
    /// Returns the row that should be drawn at visual row
    /// <paramref name="visualRow"/>, taking <see cref="ScrollOffset"/>
    /// into account. When offset = 0 this is just the active buffer's
    /// row; when scrolled up, the first N rows come from scrollback.
    /// Returns null if that visual row is above the start of scrollback.
    /// </summary>
    public TerminalCell[]? GetRowForRender(int visualRow)
    {
        // Generalized mapping works for all offsets, including a
        // negative visualRow produced by smooth-scroll (we draw one
        // row above visual 0 when PixelScrollOffset > 0).
        int sbCount     = _active.Scrollback.Count;
        int startInSb   = sbCount - ScrollOffset;
        int absoluteRow = startInSb + visualRow;

        if (absoluteRow < 0) return null;
        if (absoluteRow < sbCount) return _active.Scrollback[absoluteRow];
        int screenRow = absoluteRow - sbCount;
        return screenRow >= 0 && screenRow < Rows ? _active.GetRow(screenRow) : null;
    }

    public void Resize(int cols, int rows)
    {
        if (cols < 1 || rows < 1 || (cols == Cols && rows == Rows)) return;
        _primary.Resize(cols, rows);
        _alternate.Resize(cols, rows);
        Cols = cols; Rows = rows;
        CursorCol    = Math.Min(CursorCol, cols - 1);
        CursorRow    = Math.Min(CursorRow, rows - 1);
        ScrollTop    = 0;
        ScrollBottom = rows - 1;
        _viewport.Reset(); // viewport must follow new bottom
        _tabStops = null; // rebuild with new column count
        Bump();
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        _parser.Parse(bytes);
        Bump();
    }

    // ---- Scrollback viewport ----

    public void SetScrollOffset(int offset)
    {
        if (_viewport.SetOffset(offset, _active.Scrollback.Count)) Bump();
    }

    public void ScrollViewUp(int n)   => SetScrollOffset(_viewport.Offset + n);
    public void ScrollViewDown(int n) => SetScrollOffset(_viewport.Offset - n);

    /// <summary>Add <paramref name="pixels"/> to the scroll position
    /// (positive = scroll up into scrollback, negative = scroll toward
    /// the bottom). Crosses into whole-line bumps as the accumulated
    /// pixel distance reaches <paramref name="lineHeight"/>. Clamps to
    /// the scrollback bounds.</summary>
    public void ScrollByPixels(double pixels, double lineHeight)
    {
        if (_viewport.AddPixels(pixels, lineHeight, _active.Scrollback.Count)) Bump();
    }

    public void ResetScrollOffset()
    {
        if (_viewport.Reset()) Bump();
    }

    /// <summary>Discard the scrollback buffer entirely (Cmd+K on macOS,
    /// Ctrl+L / `clear` alternative). Snaps the view to the live
    /// screen.</summary>
    public void ClearScrollback()
    {
        _active.ClearScrollback();
        _viewport.Reset();
        Bump();
    }

    // ---- Selection ----
    // All callers pass rows in VISUAL coords (0..Rows-1) from mouse
    // events; we convert to absolute internally so the highlight is
    // anchored to content, not viewport. Scrolling after selecting
    // keeps the selection glued to the same bytes.

    /// <summary>Convert a visual row (0 = top of current viewport) to
    /// the corresponding absolute row (0 = oldest scrollback).</summary>
    public int VisualToAbsRow(int visualRow) =>
        _viewport.VisualToAbsRow(visualRow, _active.Scrollback.Count);

    public void StartSelection(int row, int col)
    {
        int abs = VisualToAbsRow(row);
        Selection = new TerminalSelection(abs, col, abs, col, SelectionMode.Character);
        Bump();
    }

    public void ExtendSelection(int row, int col)
    {
        if (Selection == null) return;
        int abs = VisualToAbsRow(row);
        Selection = Selection with { EndRow = abs, EndCol = col };
        Bump();
    }

    public void ClearSelection()
    {
        if (Selection != null) { Selection = null; Bump(); }
    }

    public void SelectWord(int row, int col)
    {
        var cells = GetRowForRender(row);
        if (cells == null) return;
        int s = col, e = col;
        while (s > 0           && IsWordChar(cells[s - 1])) s--;
        while (e < Cols - 1    && IsWordChar(cells[e + 1])) e++;
        int abs = VisualToAbsRow(row);
        Selection = new TerminalSelection(abs, s, abs, e, SelectionMode.Word);
        Bump();
    }

    public void SelectLine(int row)
    {
        int abs = VisualToAbsRow(row);
        Selection = new TerminalSelection(abs, 0, abs, Cols - 1, SelectionMode.Line);
        Bump();
    }

    /// <summary>Select every row in the buffer — scrollback + live
    /// screen. Absolute-anchored, so scrolling after Select-All keeps
    /// the same region selected.</summary>
    public void SelectAll()
    {
        int sb   = _active.Scrollback.Count;
        int last = sb + Rows - 1;
        Selection = new TerminalSelection(0, 0, last, Cols - 1, SelectionMode.Line);
        Bump();
    }

    // ---- Find / search ----

    /// <summary>
    /// Synchronous search — scans scrollback + live screen and sets
    /// <see cref="SearchMatches"/> in one pass. Used by the built-in
    /// test suite and by simple hosts that don't care about large
    /// scrollbacks; for responsive find with 1000+ row buffers, host
    /// code should use <see cref="SnapshotRows"/> + <see cref="ScanMatches"/>
    /// off-thread and apply the result via <see cref="ApplySearchResults"/>.
    /// </summary>
    public void Search(string? needle)
    {
        if (string.IsNullOrEmpty(needle))
        {
            ClearSearch();
            return;
        }
        var snap = SnapshotRows();
        var matches = SearchIndex.Scan(snap, needle, System.Threading.CancellationToken.None);
        ApplySearchResults(needle, matches);
    }

    /// <summary>Capture a snapshot of the row references (scrollback +
    /// live screen) so an off-thread scan can walk them without
    /// racing further PTY writes. The cell arrays themselves are
    /// shared — mutations to live-screen rows during the scan may
    /// show up as stale matches, which is fine: the next keystroke
    /// triggers a fresh search.</summary>
    public TerminalCell[][] SnapshotRows()
    {
        int sb = _active.Scrollback.Count;
        var snap = new TerminalCell[sb + Rows][];
        for (int i = 0; i < sb;   i++) snap[i] = _active.Scrollback[i];
        for (int i = 0; i < Rows; i++) snap[sb + i] = _active.GetRow(i);
        return snap;
    }

    /// <summary>Walk a snapshot producing every case-insensitive match
    /// of <paramref name="needle"/>. Safe to run off-thread against
    /// a <see cref="SnapshotRows"/> result. Checks
    /// <paramref name="ct"/> between rows so a superseded search
    /// returns quickly.</summary>
    public static List<SearchMatch> ScanMatches(
        TerminalCell[][] rows, string needle, System.Threading.CancellationToken ct)
        => SearchIndex.Scan(rows, needle, ct);

    /// <summary>Replace the current search results and pick the match
    /// nearest the viewport bottom so "next" moves forward from where
    /// the user is looking. Call on the UI thread.</summary>
    public void ApplySearchResults(string? needle, List<SearchMatch> matches)
    {
        int viewBottom = _active.Scrollback.Count + Rows - 1 - ScrollOffset;
        _search.Set(needle, matches, viewBottom);
        if (_search.Matches.Count > 0) ScrollCurrentMatchIntoView();
        Bump();
    }

    /// <summary>Advance to the next match, wrapping at the end.</summary>
    public void NextMatch()
    {
        if (_search.Matches.Count == 0) return;
        _search.Next();
        ScrollCurrentMatchIntoView();
        Bump();
    }

    /// <summary>Go to the previous match, wrapping at the start.</summary>
    public void PrevMatch()
    {
        if (_search.Matches.Count == 0) return;
        _search.Prev();
        ScrollCurrentMatchIntoView();
        Bump();
    }

    /// <summary>Drop the search state and hide match highlights.</summary>
    public void ClearSearch()
    {
        if (_search.Needle == null && _search.Matches.Count == 0) return;
        _search.Clear();
        Bump();
    }

    private TerminalCell[]? AbsoluteRow(int absRow, int sbCount)
    {
        if (absRow < sbCount) return _active.Scrollback[absRow];
        int screen = absRow - sbCount;
        return screen >= 0 && screen < Rows ? _active.GetRow(screen) : null;
    }

    private void ScrollCurrentMatchIntoView()
    {
        int? absRow = _search.CurrentRow;
        if (absRow == null) return;
        int sbCount = _active.Scrollback.Count;
        // Viewport shows absolute rows
        //   [sbCount + Rows - 1 - ScrollOffset - Rows + 1,
        //    sbCount + Rows - 1 - ScrollOffset]
        // → keep absRow near the middle so there's context above + below.
        int bottomAbs = sbCount + Rows - 1;
        int desired   = bottomAbs - absRow.Value - Rows / 2;
        desired = Math.Clamp(desired, 0, sbCount);
        SetScrollOffset(desired);
    }

    private static bool IsWordChar(TerminalCell c) =>
        c.Rune != 0 && c.Rune != ' ' && c.Rune != '\t';

    public string GetSelectedText()
    {
        if (Selection == null) return string.Empty;
        var (r1, c1, r2, c2) = Selection.Normalized();
        int sbCount = _active.Scrollback.Count;
        var sb = new StringBuilder();
        for (int r = r1; r <= r2; r++)
        {
            // Selection rows are absolute — row 0 is the oldest
            // scrollback line, row (sbCount + Rows - 1) is the bottom
            // of the live screen. AbsoluteRow resolves for both.
            var cells = AbsoluteRow(r, sbCount);
            if (cells == null) continue;
            int cs = r == r1 ? c1 : 0;
            int ce = r == r2 ? c2 : Cols - 1;
            for (int c = cs; c <= ce && c < cells.Length; c++)
            {
                if ((cells[c].Flags2 & CellFlags2.IsContinuation) != 0) continue;
                int rune = cells[c].Rune;
                sb.Append(rune == 0 ? ' ' : char.ConvertFromUtf32(rune));
            }
            if (r < r2) sb.Append('\n');
        }
        return sb.ToString();
    }

    public bool TryGetHyperlink(ushort id, out string url) =>
        _osc.TryGetHyperlink(id, out url);

    // ---- IParserActions ----

    public void Print(int rune)
    {
        if (rune == 0) return;

        if (_gSlots[_activeG] == Charset.DecSpecialGraphics)
            rune = DecGraphics.Translate(rune);

        int width = UnicodeWidth.Of(rune);

        // When the cursor has fallen off the right edge (past the
        // last valid column), behaviour depends on DECAWM: wrap if
        // on, stomp the right-margin cell if off.
        if (CursorCol >= Cols)
        {
            if (AutoWrap) { CarriageReturn(); LineFeedInternal(); }
            else          { CursorCol = Cols - 1; }
        }

        // Wide glyphs need two columns; wrap if the right half would
        // spill off the line (or stomp if autowrap is disabled).
        if (width == 2 && CursorCol >= Cols - 1)
        {
            if (AutoWrap) { CarriageReturn(); LineFeedInternal(); }
            else          { CursorCol = Cols - 2; }
        }

        var row  = _active.GetRow(CursorRow);
        var cell = _pen;
        cell.Rune        = rune;
        cell.HyperlinkId = _osc.ActiveLinkId;

        // IRM (insert mode): shift the row right by `width` before
        // writing. Cells pushed past the right margin are discarded.
        if (InsertMode)
            ShiftRowRight(row, CursorCol, width);

        // Clean up orphan half-cells we're about to stomp. If the
        // incoming cell lands on the continuation side of an existing
        // wide glyph, the glyph's left half must have its IsWide flag
        // dropped (otherwise the renderer will still draw it 2-col).
        // Symmetrically, if we're about to write the left half of a
        // new narrow/wide and the cell below us was a wide-left, the
        // orphaned continuation to our right must be blanked.
        if (CursorCol > 0 && (row[CursorCol].Flags2 & CellFlags2.IsContinuation) != 0)
        {
            row[CursorCol - 1].Flags2 &= ~CellFlags2.IsWide;
            row[CursorCol - 1].Rune    = 0;
        }
        if ((row[CursorCol].Flags2 & CellFlags2.IsWide) != 0 && CursorCol + 1 < Cols)
        {
            row[CursorCol + 1].Flags2 &= ~CellFlags2.IsContinuation;
        }
        // Wide-writing over a wide-left: we're placing width=2 at
        // CursorCol, which means the cell at CursorCol+1 becomes OUR
        // continuation. If CursorCol+1 was itself a wide-left (IsWide),
        // its continuation at CursorCol+2 is now orphaned — still
        // flagged IsContinuation but with no wide-left partner. Clear
        // it so the renderer doesn't treat it as an unselectable
        // phantom cell.
        if (width == 2
            && CursorCol + 1 < Cols
            && (row[CursorCol + 1].Flags2 & CellFlags2.IsWide) != 0
            && CursorCol + 2 < Cols)
        {
            row[CursorCol + 2].Flags2 &= ~CellFlags2.IsContinuation;
            row[CursorCol + 2].Rune    = 0;
        }

        // Preserve SGR-driven Flags2 bits (Blink) from the pen, but
        // override the cell-shape flags (IsWide / IsContinuation) we
        // set based on the rune width.
        var penExtras = _pen.Flags2 & CellFlags2.Blink;
        if (width == 2)
        {
            cell.Flags2 = CellFlags2.IsWide | penExtras;
            row[CursorCol] = cell;
            if (CursorCol + 1 < Cols)
            {
                var cont = _pen;
                cont.Rune        = 0;
                cont.Flags2      = CellFlags2.IsContinuation | penExtras;
                cont.HyperlinkId = _osc.ActiveLinkId;
                row[CursorCol + 1] = cont;
            }
            CursorCol += 2;
        }
        else
        {
            cell.Flags2    = penExtras;
            row[CursorCol] = cell;
            CursorCol++;
        }

        _lastPrintRune = rune;
    }

    /// <summary>Shift cells at and after <paramref name="from"/> right
    /// by <paramref name="by"/>, filling in blanks. Cells pushed off
    /// the right margin are dropped (matches xterm's IRM).</summary>
    private void ShiftRowRight(TerminalCell[] row, int from, int by)
    {
        if (by <= 0 || from >= row.Length) return;
        for (int c = row.Length - 1; c >= from + by; c--)
            row[c] = row[c - by];
        for (int c = from; c < Math.Min(from + by, row.Length); c++)
            row[c] = BlankPenCell();
    }

    public void Execute(byte c0)
    {
        switch (c0)
        {
            case 0x07: return;                                // BEL
            case 0x08: if (CursorCol > 0) CursorCol--; _lastPrintRune = 0; return; // BS
            case 0x09: HorizontalTab(); _lastPrintRune = 0; return;
            case 0x0A: case 0x0B: case 0x0C:
                // LF/VT/FF: when LNM is on, treat as NEL (CR+LF).
                if (LineFeedNewLine) CarriageReturn();
                LineFeedInternal();
                _lastPrintRune = 0;
                return;
            case 0x0D: CarriageReturn(); _lastPrintRune = 0; return;
            case 0x0E: _activeG = 1; return;                  // SO → G1
            case 0x0F: _activeG = 0; return;                  // SI → G0
        }
    }

    public void CsiDispatch(char final, ReadOnlySpan<int> p, string intermediates, char prefix)
    {
        int p0 = p.Length > 0 ? p[0] : 0;
        int p1 = p.Length > 1 ? p[1] : 0;

        if (prefix == '?')
        {
            if (final == 'h') SetDecMode(p, true);
            else if (final == 'l') SetDecMode(p, false);
            return;
        }

        // DECSTR — soft reset. Private intermediate "!" followed by 'p'.
        if (intermediates == "!" && final == 'p') { SoftReset(); return; }

        // Any non-REP CSI invalidates the "last printable" state.
        if (final != 'b') _lastPrintRune = 0;

        switch (final)
        {
            case 'A': MoveCursorRows(-Max1(p0)); return;
            case 'B': MoveCursorRows(+Max1(p0)); return;
            case 'C': CursorCol = Clamp(CursorCol + Max1(p0), 0, Cols - 1); return;
            case 'D': CursorCol = Clamp(CursorCol - Max1(p0), 0, Cols - 1); return;
            case 'E': CursorCol = 0; MoveCursorRows(+Max1(p0)); return;
            case 'F': CursorCol = 0; MoveCursorRows(-Max1(p0)); return;
            case 'G': CursorCol = Clamp((p0 > 0 ? p0 : 1) - 1, 0, Cols - 1); return;
            case 'H':
            case 'f':
                MoveCursorAbs(p0, p1);
                return;
            case 'I': CursorTabForward (Max1(p0)); return; // CHT
            case 'J': EraseDisplay(p0); return;
            case 'K': EraseLine(p0); return;
            case 'L': _active.InsertLines(CursorRow, Max1(p0), ScrollBottom); return;
            case 'M': _active.DeleteLines(CursorRow, Max1(p0), ScrollBottom); return;
            case 'P': DeleteChars(Max1(p0)); return;
            case 'S': _active.ScrollUpRegion  (ScrollTop, ScrollBottom, Max1(p0)); return;
            case 'T': _active.ScrollDownRegion(ScrollTop, ScrollBottom, Max1(p0)); return;
            case 'X': EraseChars(Max1(p0)); return;
            case 'Z': CursorTabBackward(Max1(p0)); return; // CBT
            case '@': InsertBlanks(Max1(p0)); return;
            case 'b': RepeatPrecedingChar(Max1(p0)); return; // REP
            case 'd': CursorRow = Clamp((p0 > 0 ? p0 : 1) - 1, 0, Rows - 1); return;
            case 'g': ClearTabStop(p0); return;             // TBC
            case 'h': SetAnsiMode(p, true);  return;         // SM (IRM, LNM)
            case 'l': SetAnsiMode(p, false); return;         // RM
            case 'm': ApplySgr(p); return;
            case 'n': HandleDsr(p0); return;
            case 'c': ReplyToPty("\x1b[?62;4;22c"u8); return;  // VT220 DA
            case 'r': SetScrollRegion(p0, p1); return;
            case 's': SaveCursor(); return;
            case 't': HandleWindowManip(p); return;          // CSI t — window ops
            case 'u': RestoreCursor(); return;
            case 'q': SetCursorStyle(p0); return;              // DECSCUSR (with/without SP intermediate)
        }
    }

    // ---- CUP/HVP with DECOM origin mode ----

    private void MoveCursorAbs(int row1Based, int col1Based)
    {
        int row = (row1Based > 0 ? row1Based : 1) - 1;
        int col = (col1Based > 0 ? col1Based : 1) - 1;
        if (OriginMode)
        {
            row += ScrollTop;
            CursorRow = Clamp(row, ScrollTop, ScrollBottom);
        }
        else
        {
            CursorRow = Clamp(row, 0, Rows - 1);
        }
        CursorCol = Clamp(col, 0, Cols - 1);
    }

    public void EscDispatch(char final, string intermediates)
    {
        // SCS: ESC ( X selects G0 charset; ESC ) X selects G1.
        if (intermediates is "(" or ")")
        {
            int slot = intermediates == "(" ? 0 : 1;
            _gSlots[slot] = final == '0' ? Charset.DecSpecialGraphics : Charset.Ascii;
            return;
        }
        _lastPrintRune = 0; // anything here is a control dispatch
        switch (final)
        {
            case '7': SaveCursor(); return;                    // DECSC
            case '8': RestoreCursor(); return;                 // DECRC
            case '=': ApplicationKeypad = true;  return;       // DECKPAM
            case '>': ApplicationKeypad = false; return;       // DECKPNM
            case 'D': LineFeedInternal(); return;              // IND
            case 'E': CarriageReturn(); LineFeedInternal(); return; // NEL
            case 'H': SetTabStop(CursorCol); return;           // HTS
            case 'M': ReverseIndex(); return;                  // RI
            case 'c': FullReset(); return;                     // RIS
        }
    }

    public void OscDispatch(string payload) => _osc.Dispatch(payload);

    public void ReplyToPty(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes) _pendingReplies.Add(b);
    }

    // ---- Implementation helpers ----

    private void SetScrollRegion(int top, int bottom)
    {
        int t = top    > 0 ? top    - 1 : 0;
        int b = bottom > 0 ? bottom - 1 : Rows - 1;
        if (t >= b || b >= Rows) { t = 0; b = Rows - 1; }
        ScrollTop    = t;
        ScrollBottom = b;
        // DECSTBM parks the cursor at home (origin-mode respecting).
        CursorCol = 0;
        CursorRow = OriginMode ? ScrollTop : 0;
    }

    // ---- ANSI mode (CSI h/l without `?`): IRM, LNM ----

    private void SetAnsiMode(ReadOnlySpan<int> p, bool on)
    {
        foreach (var m in p)
        {
            switch (m)
            {
                case 4:  InsertMode        = on; break;
                case 20: LineFeedNewLine   = on; break;
            }
        }
    }

    // ---- Tab stops ----

    private bool[] EnsureTabStops()
    {
        if (_tabStops == null || _tabStops.Length != Cols)
        {
            _tabStops = new bool[Cols];
            for (int c = 8; c < Cols; c += 8) _tabStops[c] = true;
        }
        return _tabStops;
    }

    private void SetTabStop(int col)
    {
        var ts = EnsureTabStops();
        if (col >= 0 && col < ts.Length) ts[col] = true;
    }

    private void ClearTabStop(int mode)
    {
        var ts = EnsureTabStops();
        switch (mode)
        {
            case 0: if (CursorCol >= 0 && CursorCol < ts.Length) ts[CursorCol] = false; break;
            case 3: Array.Clear(ts, 0, ts.Length); break;
        }
    }

    private void CursorTabForward(int n)
    {
        var ts = EnsureTabStops();
        while (n-- > 0 && CursorCol < Cols - 1)
        {
            int next = CursorCol + 1;
            while (next < Cols - 1 && !ts[next]) next++;
            CursorCol = next;
        }
    }

    private void CursorTabBackward(int n)
    {
        var ts = EnsureTabStops();
        while (n-- > 0 && CursorCol > 0)
        {
            int prev = CursorCol - 1;
            while (prev > 0 && !ts[prev]) prev--;
            CursorCol = prev;
        }
    }

    // ---- REP ----

    private void RepeatPrecedingChar(int count)
    {
        if (_lastPrintRune == 0) return;
        int rune = _lastPrintRune;
        for (int i = 0; i < count; i++) Print(rune);
    }

    // ---- DECSTR (soft reset) ----

    private void SoftReset()
    {
        CursorVisible = true;
        ScrollTop     = 0;
        ScrollBottom  = Rows - 1;
        InsertMode    = false;
        OriginMode    = false;
        _pen = TerminalCell.Blank;
        _primarySaved   = default;
        _alternateSaved = default;
        _gSlots[0] = Charset.Ascii; _gSlots[1] = Charset.Ascii;
        _activeG = 0;
    }

    // ---- Window manipulation CSI t — safe subset ----

    private void HandleWindowManip(ReadOnlySpan<int> p)
    {
        // We implement only the reporting operations; anything that
        // would change the host window (resize, raise, iconify) is
        // ignored on purpose — those belong to the host shell.
        int op = p.Length > 0 ? p[0] : 0;
        switch (op)
        {
            case 14: // report window size in pixels — respond with cell×cell approximation
                ReplyAscii($"\x1b[4;{Rows * 16};{Cols * 8}t");
                return;
            case 16: // report cell size in pixels (approximate)
                ReplyAscii("\x1b[6;16;8t");
                return;
            case 18: // report text area size in characters
                ReplyAscii($"\x1b[8;{Rows};{Cols}t");
                return;
            case 19: // report screen size in characters (assume same as text area)
                ReplyAscii($"\x1b[9;{Rows};{Cols}t");
                return;
            case 20: // report icon name via OSC L
                ReplyAscii("\x1b]L" + _osc.WindowTitle + "\x1b\\");
                return;
            case 21: // report window title via OSC l
                ReplyAscii("\x1b]l" + _osc.WindowTitle + "\x1b\\");
                return;
            // All other ops (resize, move, raise, etc.) are no-ops.
        }
    }

    // ---- OSC 4: palette entry query/set. OSC 10/11/12: fg/bg/cursor ----

    private void ReplyAscii(string s) => ReplyToPty(Encoding.ASCII.GetBytes(s));

    private void SetDecMode(ReadOnlySpan<int> p, bool on)
    {
        foreach (var m in p)
        {
            switch (m)
            {
                case 1:    ApplicationCursorKeys = on; break;
                case 5:    ReverseVideo = on; break;              // DECSCNM
                case 6:                                            // DECOM
                    OriginMode = on;
                    // Entering origin mode parks the cursor at the
                    // top of the region; leaving it returns to home.
                    CursorRow = on ? ScrollTop : 0;
                    CursorCol = 0;
                    break;
                case 7:    AutoWrap = on; break;                   // DECAWM
                case 25:   CursorVisible = on; break;
                case 47: case 1047: case 1049:
                    if (on) EnterAlt(m == 1049); else LeaveAlt(m == 1049);
                    break;
                case 1000: MouseMode = on ? 1000 : 0; break;
                case 1002: MouseMode = on ? 1002 : 0; break;
                case 1003: MouseMode = on ? 1003 : 0; break;
                case 1004: FocusEvents = on; break;
                case 2004: BracketedPaste = on; break;
            }
        }
    }

    private void SetCursorStyle(int p) =>
        CursorStyle = p switch
        {
            0 or 1 => CursorStyle.BlockBlink,
            2      => CursorStyle.Block,
            3      => CursorStyle.UnderlineBlink,
            4      => CursorStyle.Underline,
            5      => CursorStyle.BarBlink,
            6      => CursorStyle.Bar,
            _      => CursorStyle.BlockBlink,
        };

    private void HandleDsr(int p)
    {
        if (p == 5) { ReplyToPty("\x1b[0n"u8); return; }
        if (p == 6)
        {
            var s = Encoding.ASCII.GetBytes($"\x1b[{CursorRow + 1};{CursorCol + 1}R");
            ReplyToPty(s);
        }
    }

    private void EnterAlt(bool saveCursor)
    {
        if (saveCursor) _alternateSaved = SnapshotCursor();
        if (_active == _alternate) return;
        _active = _alternate;
        _active.Clear();
        ScrollTop = 0; ScrollBottom = Rows - 1;
    }

    private void LeaveAlt(bool restoreCursor)
    {
        if (_active != _alternate) return;
        _active = _primary;
        ScrollTop = 0; ScrollBottom = Rows - 1;
        if (restoreCursor) ApplyCursor(_alternateSaved);
    }

    private void CarriageReturn() => CursorCol = 0;

    private void LineFeedInternal()
    {
        if (CursorRow < ScrollBottom)
            CursorRow++;
        else if (CursorRow == ScrollBottom)
            _active.ScrollUpRegion(ScrollTop, ScrollBottom, 1);
        else if (CursorRow < Rows - 1)
            CursorRow++;
        // Cursor below ScrollBottom at the last row: no scroll, no move.
    }

    private void HorizontalTab()
    {
        // Use the custom tab-stop array. Falls back to every-8-cols
        // stops when no HTS/TBC has customised anything.
        var ts = EnsureTabStops();
        int next = CursorCol + 1;
        while (next < Cols - 1 && !ts[next]) next++;
        CursorCol = Math.Min(next, Cols - 1);
    }

    private void ReverseIndex()
    {
        if (CursorRow > ScrollTop) CursorRow--;
        else                        _active.ScrollDownRegion(ScrollTop, ScrollBottom, 1);
    }

    private void MoveCursorRows(int dy) => CursorRow = Clamp(CursorRow + dy, 0, Rows - 1);

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0: EraseLine(0); for (int r = CursorRow + 1; r < Rows;      r++) ClearRow(r); break;
            case 1: EraseLine(1); for (int r = 0;              r < CursorRow; r++) ClearRow(r); break;
            case 2: for (int r = 0; r < Rows; r++) ClearRow(r); break;
            case 3:
                // xterm: mode 3 clears the scrollback buffer but
                // leaves the visible screen intact. Linux ED 3 extension.
                ClearScrollback();
                break;
        }
    }

    private void EraseLine(int mode)
    {
        var row = _active.GetRow(CursorRow);
        switch (mode)
        {
            case 0: for (int c = CursorCol;             c < Cols;         c++) row[c] = BlankPenCell(); break;
            case 1: for (int c = 0;                     c <= CursorCol && c < Cols; c++) row[c] = BlankPenCell(); break;
            case 2: for (int c = 0;                     c < Cols;         c++) row[c] = BlankPenCell(); break;
        }
    }

    private void ClearRow(int r)
    {
        var row = _active.GetRow(r);
        for (int c = 0; c < Cols; c++) row[c] = BlankPenCell();
    }

    private void EraseChars(int n)
    {
        var row = _active.GetRow(CursorRow);
        for (int i = 0; i < n && CursorCol + i < Cols; i++) row[CursorCol + i] = BlankPenCell();
    }

    private void DeleteChars(int n)
    {
        var row = _active.GetRow(CursorRow);
        int src = CursorCol + n, dst = CursorCol;
        while (src < Cols) row[dst++] = row[src++];
        while (dst < Cols) row[dst++] = BlankPenCell();
    }

    private void InsertBlanks(int n)
    {
        var row = _active.GetRow(CursorRow);
        for (int c = Cols - 1;            c >= CursorCol + n; c--) row[c] = row[c - n];
        for (int c = CursorCol; c < CursorCol + n && c < Cols; c++) row[c] = BlankPenCell();
    }

    private void SaveCursor()
    {
        if (_active == _alternate) _alternateSaved = SnapshotCursor();
        else                        _primarySaved   = SnapshotCursor();
    }

    private void RestoreCursor() =>
        ApplyCursor(_active == _alternate ? _alternateSaved : _primarySaved);

    private void FullReset()
    {
        _active = _primary;
        _primary.Clear();
        _alternate.Clear();
        CursorRow = CursorCol = 0;
        _pen = TerminalCell.Blank;
        CursorVisible = true;
        CursorStyle = CursorStyle.BlockBlink;
        ScrollTop = 0; ScrollBottom = Rows - 1;
        _activeG = 0; _gSlots[0] = Charset.Ascii; _gSlots[1] = Charset.Ascii;
        MouseMode = 0; BracketedPaste = false; FocusEvents = false;
        ApplicationCursorKeys = false; ApplicationKeypad = false;
        AutoWrap = true; OriginMode = false; ReverseVideo = false;
        InsertMode = false; LineFeedNewLine = false;
        _viewport.Reset(); Selection = null;
        _osc.Reset();
        _tabStops = null; // will rebuild with defaults on next access
        _lastPrintRune = 0;
        _parser.Reset();
    }

    // ---- SGR ----

    private void ApplySgr(ReadOnlySpan<int> p)
    {
        if (p.Length == 0) { _pen = TerminalCell.Blank; return; }

        int i = 0;
        while (i < p.Length)
        {
            switch (p[i])
            {
                case 0:   _pen = TerminalCell.Blank; break;
                case 1:   _pen.Flags  |=  CellFlags.Bold;          break;
                case 2:   _pen.Flags  |=  CellFlags.Dim;           break;
                case 3:   _pen.Flags  |=  CellFlags.Italic;        break;
                case 4:   _pen.Flags  |=  CellFlags.Underline;     break;
                case 5:
                case 6:   _pen.Flags2 |=  CellFlags2.Blink;        break;
                case 7:   _pen.Flags  |=  CellFlags.Inverse;       break;
                case 9:   _pen.Flags  |=  CellFlags.Strikethrough; break;
                case 22:  _pen.Flags  &= ~(CellFlags.Bold | CellFlags.Dim); break;
                case 23:  _pen.Flags  &= ~CellFlags.Italic;        break;
                case 24:  _pen.Flags  &= ~CellFlags.Underline;     break;
                case 25:  _pen.Flags2 &= ~CellFlags2.Blink;        break;
                case 27:  _pen.Flags  &= ~CellFlags.Inverse;       break;
                case 29:  _pen.Flags  &= ~CellFlags.Strikethrough; break;

                case 30: case 31: case 32: case 33:
                case 34: case 35: case 36: case 37:
                    SetFgIdx((byte)(p[i] - 30)); break;
                case 39: ClearFg(); break;

                case 40: case 41: case 42: case 43:
                case 44: case 45: case 46: case 47:
                    SetBgIdx((byte)(p[i] - 40)); break;
                case 49: ClearBg(); break;

                case 90: case 91: case 92: case 93:
                case 94: case 95: case 96: case 97:
                    SetFgIdx((byte)(p[i] - 90 + 8)); break;
                case 100: case 101: case 102: case 103:
                case 104: case 105: case 106: case 107:
                    SetBgIdx((byte)(p[i] - 100 + 8)); break;

                case 38: i += ApplyExtColor(p, i, true);  break;
                case 48: i += ApplyExtColor(p, i, false); break;
            }
            i++;
        }
    }

    /// <summary>Returns the number of params beyond <paramref name="i"/>
    /// the caller should skip (0 / 2 / 4 depending on 256 vs RGB).</summary>
    private int ApplyExtColor(ReadOnlySpan<int> p, int i, bool fg)
    {
        if (i + 1 >= p.Length) return 0;
        int kind = p[i + 1];
        if (kind == 5 && i + 2 < p.Length)
        {
            byte idx = (byte)(p[i + 2] & 0xFF);
            if (fg) SetFgIdx(idx); else SetBgIdx(idx);
            return 2;
        }
        if (kind == 2 && i + 4 < p.Length)
        {
            uint packed = (uint)((p[i + 2] << 16) | (p[i + 3] << 8) | p[i + 4]);
            if (fg) { _pen.FgRgb = packed; _pen.Flags |= CellFlags.FgRgb; }
            else    { _pen.BgRgb = packed; _pen.Flags |= CellFlags.BgRgb; }
            return 4;
        }
        return 0;
    }

    private void SetFgIdx(byte i) { _pen.FgIndex = i; _pen.FgRgb = 0; _pen.Flags &= ~CellFlags.FgRgb; }
    private void SetBgIdx(byte i) { _pen.BgIndex = i; _pen.BgRgb = 0; _pen.Flags &= ~CellFlags.BgRgb; }
    private void ClearFg() { _pen.FgIndex = 0; _pen.FgRgb = 0; _pen.Flags &= ~CellFlags.FgRgb; }
    private void ClearBg() { _pen.BgIndex = 0; _pen.BgRgb = 0; _pen.Flags &= ~CellFlags.BgRgb; }

    private TerminalCell BlankPenCell()
    {
        // Blank cells carry the current background so EL/ED with the
        // current pen paints a swath of the current bg colour.
        var c = TerminalCell.Blank;
        c.BgIndex = _pen.BgIndex;
        c.BgRgb   = _pen.BgRgb;
        c.Flags   = _pen.Flags & CellFlags.BgRgb;
        return c;
    }

    private static int Max1(int n)                    => n > 0 ? n : 1;
    private static int Clamp(int v, int lo, int hi)   => v < lo ? lo : v > hi ? hi : v;
    /// <summary>Fires after any state change that should trigger a
    /// repaint. Hosts (e.g. TerminalControl) subscribe once rather than
    /// guarding every mutation site with an InvalidateVisual.</summary>
    public event EventHandler? Changed;

    private void Bump()
    {
        unchecked { Revision++; }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
