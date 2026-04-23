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

    /// <summary>SGR pen applied to every <see cref="Print"/>.</summary>
    public TerminalCell PenTemplate = TerminalCell.Blank;

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

    // Saved cursor state — primary and alternate buffers each keep
    // their own snapshot so DECSC/DECRC while toggling alt screens
    // doesn't clobber the other buffer's saved position.
    private int _savedRow, _savedCol;
    private TerminalCell _savedPen;
    private int _altSavedRow, _altSavedCol;
    private TerminalCell _altSavedPen;

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
    public bool AllowClipboardAccess  { get; set; }

    /// <summary>Default foreground reported to OSC 10 queries. Packed
    /// as 0xRRGGBB. Host layers that theme the terminal (e.g. the
    /// VibeCoder app) update this when the theme changes.</summary>
    public uint DefaultForegroundRgb { get; set; } = 0xD0D0D0;

    /// <summary>Default background for OSC 11 queries.</summary>
    public uint DefaultBackgroundRgb { get; set; } = 0x1E1E1E;

    /// <summary>Cursor colour for OSC 12 queries.</summary>
    public uint DefaultCursorRgb     { get; set; } = 0xD0D0D0;

    /// <summary>Current 256-palette colours for OSC 4 queries. Array
    /// is lazily populated on first read to avoid a static table
    /// that would couple this layer to the renderer's theme.</summary>
    private uint[]? _palette256;

    // Scrollback viewport. 0 = at bottom; positive = scrolled up into
    // scrollback. TerminalControl resets this to 0 on any keystroke.
    // <see cref="PixelScrollOffset"/> carries the sub-line pixel
    // remainder so the renderer can slide content smoothly — wheel
    // events accumulate in pixel space and turn over into whole-line
    // <see cref="ScrollOffset"/> bumps as they cross a line height.
    public int ScrollOffset { get; private set; }
    public double PixelScrollOffset { get; private set; }

    public TerminalSelection? Selection { get; private set; }

    // ---- Find / search state ----
    // Matches are stored in ABSOLUTE row coordinates: row 0 is the
    // oldest scrollback row, row (ScrollbackCount + Rows - 1) is the
    // bottom visible row. Rendering and navigation map these into
    // whatever visual rows the current ScrollOffset is showing — stable
    // even as the user scrolls.
    public readonly record struct SearchMatch(int Row, int Col, int Length);

    public string? SearchNeedle { get; private set; }
    private readonly List<SearchMatch> _matches = new();
    public IReadOnlyList<SearchMatch> SearchMatches => _matches;
    public int CurrentMatchIndex { get; private set; } = -1;

    // OSC 8 hyperlinks.
    private readonly Dictionary<ushort, string> _hyperlinks = new();
    private ushort _nextHyperlinkId = 1;
    private ushort _activeLinkId;

    // UTF-8 partial state for split chunks (unused now that the parser
    // handles it, but kept for future external Write(byte) callers).
    private readonly List<byte> _pendingReplies = new();

    // Last printable codepoint emitted — used by REP (CSI Ps b) to
    // repeat the preceding character. Reset to 0 on any control
    // sequence other than REP itself so REP after e.g. a newline is
    // a no-op, matching xterm.js's <c>precedingJoinState</c>.
    private int _lastPrintRune;
    private int _lastPrintWidth;

    // Custom tab stops. When null, defaults to every 8 cols.
    // HTS (ESC H) adds a stop, TBC (CSI g) clears.
    private bool[]? _tabStops;

    // Window title buffer. Most recent OSC 0/1/2 payload — used to
    // reply to CSI 21 t (report title).
    private string _windowTitle = string.Empty;

    public byte[]? TakeReplies()
    {
        if (_pendingReplies.Count == 0) return null;
        var b = _pendingReplies.ToArray();
        _pendingReplies.Clear();
        return b;
    }

    /// <summary>Fired when an OSC 0 or OSC 2 sets the window title.</summary>
    public event EventHandler<string>? TitleChanged;

    /// <summary>Fired when OSC 0 or OSC 1 sets the icon name. Most
    /// shells emit OSC 0 which sets both title and icon name.</summary>
    public event EventHandler<string>? IconNameChanged;

    /// <summary>Fired when an OSC 52 ; c ; &lt;base64&gt; request
    /// arrives AND <see cref="AllowClipboardAccess"/> is true. The
    /// host decides whether to honour (copy to clipboard) or ignore.</summary>
    public event EventHandler<ClipboardRequestEventArgs>? ClipboardRequested;

    public sealed class ClipboardRequestEventArgs : EventArgs
    {
        public string Text { get; }
        public ClipboardRequestEventArgs(string text) { Text = text; }
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
        ScrollBottom = rows - 1;
    }

    public TerminalCell[] GetVisibleRow(int r) => _active.GetRow(r);

    public IEnumerable<TerminalCell[]> AllRows()
    {
        foreach (var r in _active.Scrollback) yield return r;
        for (int i = 0; i < Rows; i++) yield return _active.GetRow(i);
    }

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
        if (absoluteRow < sbCount)
        {
            int idx = 0;
            foreach (var row in _active.Scrollback)
            {
                if (idx++ == absoluteRow) return row;
            }
            return null;
        }
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
        ScrollOffset = 0; // viewport must follow new bottom
        PixelScrollOffset = 0;
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
        int clamped = Math.Clamp(offset, 0, _active.Scrollback.Count);
        if (clamped != ScrollOffset || PixelScrollOffset != 0)
        {
            ScrollOffset = clamped;
            PixelScrollOffset = 0;
            Bump();
        }
    }

    public void ScrollViewUp(int n)   => SetScrollOffset(ScrollOffset + n);
    public void ScrollViewDown(int n) => SetScrollOffset(ScrollOffset - n);

    /// <summary>
    /// Add <paramref name="pixels"/> to the scroll position (positive =
    /// scroll up into scrollback, negative = scroll toward bottom).
    /// Crosses into whole-line <see cref="ScrollOffset"/> bumps as the
    /// accumulated pixel distance reaches <paramref name="lineHeight"/>.
    /// Clamps to the scrollback bounds.
    /// </summary>
    public void ScrollByPixels(double pixels, double lineHeight)
    {
        if (lineHeight <= 0) return;
        double total  = ScrollOffset * lineHeight + PixelScrollOffset + pixels;
        double maxTot = _active.Scrollback.Count * lineHeight;
        total = Math.Clamp(total, 0.0, maxTot);

        int   newOffset = (int)(total / lineHeight);
        double newPixel = total - newOffset * lineHeight;
        if (newOffset != ScrollOffset || Math.Abs(newPixel - PixelScrollOffset) > 0.01)
        {
            ScrollOffset      = newOffset;
            PixelScrollOffset = newPixel;
            Bump();
        }
    }

    public void ResetScrollOffset()
    {
        if (ScrollOffset != 0 || PixelScrollOffset != 0)
        {
            ScrollOffset = 0;
            PixelScrollOffset = 0;
            Bump();
        }
    }

    /// <summary>Discard the scrollback buffer entirely (Cmd+K on macOS,
    /// Ctrl+L / `clear` alternative). Snaps the view to the live
    /// screen.</summary>
    public void ClearScrollback()
    {
        _active.ClearScrollback();
        ScrollOffset = 0;
        PixelScrollOffset = 0;
        Bump();
    }

    // ---- Selection ----

    public void StartSelection(int row, int col)
    {
        Selection = new TerminalSelection(row, col, row, col, SelectionMode.Character);
        Bump();
    }

    public void ExtendSelection(int row, int col)
    {
        if (Selection == null) return;
        Selection = Selection with { EndRow = row, EndCol = col };
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
        Selection = new TerminalSelection(row, s, row, e, SelectionMode.Word);
        Bump();
    }

    public void SelectLine(int row)
    {
        Selection = new TerminalSelection(row, 0, row, Cols - 1, SelectionMode.Line);
        Bump();
    }

    /// <summary>Select every visible row in the current viewport. If
    /// the user is viewing scrollback, this selects that region; at
    /// the live prompt it selects the visible screen.</summary>
    public void SelectAll()
    {
        Selection = new TerminalSelection(0, 0, Rows - 1, Cols - 1, SelectionMode.Line);
        Bump();
    }

    // ---- Find / search ----

    /// <summary>
    /// Populate <see cref="SearchMatches"/> with every case-insensitive
    /// occurrence of <paramref name="needle"/> across scrollback + live
    /// screen. Empty needle clears the match list.
    /// <see cref="CurrentMatchIndex"/> is set to the match closest to
    /// the current viewport so <see cref="NextMatch"/> feels natural.
    /// </summary>
    public void Search(string? needle)
    {
        SearchNeedle = string.IsNullOrEmpty(needle) ? null : needle;
        _matches.Clear();
        CurrentMatchIndex = -1;

        if (SearchNeedle == null) { Bump(); return; }

        int sbCount = _active.Scrollback.Count;
        int totalRows = sbCount + Rows;
        for (int absRow = 0; absRow < totalRows; absRow++)
        {
            TerminalCell[]? row = AbsoluteRow(absRow, sbCount);
            if (row == null) continue;
            FindInRow(row, absRow, SearchNeedle, _matches);
        }

        if (_matches.Count > 0)
        {
            // Pick the match nearest the current viewport bottom so
            // "next" moves forward from where the user is looking.
            int viewBottom = sbCount + Rows - 1 - ScrollOffset;
            CurrentMatchIndex = NearestMatchIndex(viewBottom);
            ScrollCurrentMatchIntoView();
        }
        Bump();
    }

    /// <summary>Advance to the next match, wrapping at the end.</summary>
    public void NextMatch()
    {
        if (_matches.Count == 0) return;
        CurrentMatchIndex = (CurrentMatchIndex + 1) % _matches.Count;
        ScrollCurrentMatchIntoView();
        Bump();
    }

    /// <summary>Go to the previous match, wrapping at the start.</summary>
    public void PrevMatch()
    {
        if (_matches.Count == 0) return;
        CurrentMatchIndex = (CurrentMatchIndex - 1 + _matches.Count) % _matches.Count;
        ScrollCurrentMatchIntoView();
        Bump();
    }

    /// <summary>Drop the search state and hide match highlights.</summary>
    public void ClearSearch()
    {
        if (SearchNeedle == null && _matches.Count == 0) return;
        SearchNeedle = null;
        _matches.Clear();
        CurrentMatchIndex = -1;
        Bump();
    }

    private TerminalCell[]? AbsoluteRow(int absRow, int sbCount)
    {
        if (absRow < sbCount)
        {
            int i = 0;
            foreach (var r in _active.Scrollback)
                if (i++ == absRow) return r;
            return null;
        }
        int screen = absRow - sbCount;
        return screen >= 0 && screen < Rows ? _active.GetRow(screen) : null;
    }

    private static void FindInRow(TerminalCell[] row, int absRow,
        string needle, List<SearchMatch> into)
    {
        // Decode cells to a string so multi-cell wide glyphs and runs
        // of blanks search naturally. Column indices map 1:1 with cell
        // slots including wide-cell continuations.
        var sb = new StringBuilder(row.Length);
        for (int i = 0; i < row.Length; i++)
        {
            int rune = row[i].Rune;
            sb.Append(rune == 0 ? ' ' : (char)Math.Min(rune, 0xFFFF));
        }
        var haystack = sb.ToString();
        int from = 0;
        while (from <= haystack.Length - needle.Length)
        {
            int idx = haystack.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;
            into.Add(new SearchMatch(absRow, idx, needle.Length));
            from = idx + Math.Max(1, needle.Length);
        }
    }

    private int NearestMatchIndex(int absRowNear)
    {
        int best = 0, bestDist = int.MaxValue;
        for (int i = 0; i < _matches.Count; i++)
        {
            int d = Math.Abs(_matches[i].Row - absRowNear);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    private void ScrollCurrentMatchIntoView()
    {
        if (CurrentMatchIndex < 0 || CurrentMatchIndex >= _matches.Count) return;
        int sbCount = _active.Scrollback.Count;
        int absRow  = _matches[CurrentMatchIndex].Row;

        // Desired scroll offset: want the match at absRow to be
        // visible. Viewport shows absolute rows
        //   [sbCount + Rows - 1 - ScrollOffset - Rows + 1,
        //    sbCount + Rows - 1 - ScrollOffset]
        // → keep absRow somewhere in the middle. Aim for middle of view.
        int bottomAbs = sbCount + Rows - 1;
        int desired   = bottomAbs - absRow - Rows / 2;
        desired = Math.Clamp(desired, 0, sbCount);
        SetScrollOffset(desired);
    }

    private static bool IsWordChar(TerminalCell c) =>
        c.Rune != 0 && c.Rune != ' ' && c.Rune != '\t';

    public string GetSelectedText()
    {
        if (Selection == null) return string.Empty;
        var (r1, c1, r2, c2) = Selection.Normalized();
        var sb = new StringBuilder();
        for (int r = r1; r <= r2; r++)
        {
            var cells = GetRowForRender(r);
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
        _hyperlinks.TryGetValue(id, out url!);

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
        var cell = PenTemplate;
        cell.Rune        = rune;
        cell.HyperlinkId = _activeLinkId;

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

        // Preserve SGR-driven Flags2 bits (Blink) from the pen, but
        // override the cell-shape flags (IsWide / IsContinuation) we
        // set based on the rune width.
        var penExtras = PenTemplate.Flags2 & CellFlags2.Blink;
        if (width == 2)
        {
            cell.Flags2 = CellFlags2.IsWide | penExtras;
            row[CursorCol] = cell;
            if (CursorCol + 1 < Cols)
            {
                var cont = PenTemplate;
                cont.Rune        = 0;
                cont.Flags2      = CellFlags2.IsContinuation | penExtras;
                cont.HyperlinkId = _activeLinkId;
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

        _lastPrintRune  = rune;
        _lastPrintWidth = width;
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

    public void CsiDispatch(char final, int[] p, string intermediates, char prefix)
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

    public void OscDispatch(string payload)
    {
        int semi = payload.IndexOf(';');
        if (semi < 0) return;
        if (!int.TryParse(payload.AsSpan(0, semi), out int cmd)) return;
        var data = payload.Substring(semi + 1);
        switch (cmd)
        {
            case 0:
                // OSC 0 sets both window title and icon name.
                _windowTitle = data;
                TitleChanged?.Invoke(this, data);
                IconNameChanged?.Invoke(this, data);
                return;
            case 1:
                IconNameChanged?.Invoke(this, data);
                return;
            case 2:
                _windowTitle = data;
                TitleChanged?.Invoke(this, data);
                return;
            case 4:  HandleOsc4 (data); return;
            case 8:  HandleOsc8 (data); return;
            case 10: HandleOscSpecialColor(10, () => DefaultForegroundRgb, v => DefaultForegroundRgb = v, data); return;
            case 11: HandleOscSpecialColor(11, () => DefaultBackgroundRgb, v => DefaultBackgroundRgb = v, data); return;
            case 12: HandleOscSpecialColor(12, () => DefaultCursorRgb,     v => DefaultCursorRgb     = v, data); return;
            case 52: HandleOsc52(data); return;
        }
    }

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

    private void SetAnsiMode(int[] p, bool on)
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
        PenTemplate   = TerminalCell.Blank;
        _savedRow = _savedCol = 0; _savedPen = TerminalCell.Blank;
        _altSavedRow = _altSavedCol = 0; _altSavedPen = TerminalCell.Blank;
        _gSlots[0] = Charset.Ascii; _gSlots[1] = Charset.Ascii;
        _activeG = 0;
    }

    // ---- Window manipulation CSI t — safe subset ----

    private void HandleWindowManip(int[] p)
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
                ReplyAscii("\x1b]L" + _windowTitle + "\x1b\\");
                return;
            case 21: // report window title via OSC l
                ReplyAscii("\x1b]l" + _windowTitle + "\x1b\\");
                return;
            // All other ops (resize, move, raise, etc.) are no-ops.
        }
    }

    // ---- OSC 4: palette entry query/set. OSC 10/11/12: fg/bg/cursor ----

    private uint[] EnsurePalette256()
    {
        if (_palette256 != null) return _palette256;
        _palette256 = new uint[256];
        // Standard xterm 256 palette. Same numbers we reference in
        // the renderer (see Render/TerminalPalette.cs) so queries are
        // consistent with what's on screen.
        uint[] basic =
        {
            0x000000, 0x800000, 0x008000, 0x808000,
            0x000080, 0x800080, 0x008080, 0xC0C0C0,
            0x808080, 0xFF0000, 0x00FF00, 0xFFFF00,
            0x0000FF, 0xFF00FF, 0x00FFFF, 0xFFFFFF,
        };
        for (int i = 0; i < 16; i++) _palette256[i] = basic[i];
        int idx = 16;
        int[] levels = { 0, 95, 135, 175, 215, 255 };
        for (int r = 0; r < 6; r++)
        for (int g = 0; g < 6; g++)
        for (int b = 0; b < 6; b++)
            _palette256[idx++] = (uint)((levels[r] << 16) | (levels[g] << 8) | levels[b]);
        for (int i = 0; i < 24; i++)
        {
            int v = 8 + i * 10;
            _palette256[idx++] = (uint)((v << 16) | (v << 8) | v);
        }
        return _palette256;
    }

    private void HandleOsc4(string data)
    {
        // Payload is "idx;spec[;idx;spec...]". If spec == "?" we reply
        // with the current rgb: form; otherwise parse + set.
        var parts = data.Split(';');
        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            if (!int.TryParse(parts[i], out int idx) || idx < 0 || idx > 255) continue;
            var spec = parts[i + 1];
            if (spec == "?")
            {
                var pal = EnsurePalette256();
                ReplyAscii($"\x1b]4;{idx};{RgbSpec(pal[idx])}\x1b\\");
            }
            else if (TryParseRgbSpec(spec, out var rgb))
            {
                EnsurePalette256()[idx] = rgb;
            }
        }
    }

    private void HandleOscSpecialColor(int cmd, Func<uint> getter, Action<uint> setter, string data)
    {
        // Data is either "?" (query) or an rgb:RRRR/GGGG/BBBB (or
        // #RRGGBB) spec to set.
        if (data == "?")
        {
            ReplyAscii($"\x1b]{cmd};{RgbSpec(getter())}\x1b\\");
        }
        else if (TryParseRgbSpec(data, out var rgb))
        {
            setter(rgb);
        }
    }

    private void HandleOsc52(string data)
    {
        // Syntax: "clipboards;payload". Payload is base64 for set or
        // "?" for get. We gate both behind AllowClipboardAccess; get
        // isn't wired (the host decides whether to leak data back).
        if (!AllowClipboardAccess) return;
        int semi = data.IndexOf(';');
        if (semi < 0) return;
        var body = data.Substring(semi + 1);
        if (body == "?") return; // get-from-clipboard requests are ignored
        string decoded;
        try
        {
            var bytes = Convert.FromBase64String(body);
            decoded = Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            return;
        }
        ClipboardRequested?.Invoke(this, new ClipboardRequestEventArgs(decoded));
    }

    private static string RgbSpec(uint rgb)
    {
        int r = (int)((rgb >> 16) & 0xFF);
        int g = (int)((rgb >>  8) & 0xFF);
        int b = (int)( rgb        & 0xFF);
        // xterm replies with 16-bit components so most consumers work
        // even when they expect the VT5xx format. Repeat the 8-bit
        // value in both halves (e.g. 0xAB → 0xABAB).
        return $"rgb:{r:x2}{r:x2}/{g:x2}{g:x2}/{b:x2}{b:x2}";
    }

    private static bool TryParseRgbSpec(string spec, out uint rgb)
    {
        rgb = 0;
        if (spec.StartsWith('#') && (spec.Length == 7 || spec.Length == 13))
        {
            // #RRGGBB or #RRRRGGGGBBBB — take the high byte of each.
            int step = spec.Length == 7 ? 2 : 4;
            if (!int.TryParse(spec.AsSpan(1,         2), System.Globalization.NumberStyles.HexNumber, null, out int r)) return false;
            if (!int.TryParse(spec.AsSpan(1+step,    2), System.Globalization.NumberStyles.HexNumber, null, out int g)) return false;
            if (!int.TryParse(spec.AsSpan(1+step*2,  2), System.Globalization.NumberStyles.HexNumber, null, out int b)) return false;
            rgb = (uint)((r << 16) | (g << 8) | b);
            return true;
        }
        if (spec.StartsWith("rgb:", StringComparison.Ordinal))
        {
            var parts = spec.Substring(4).Split('/');
            if (parts.Length != 3) return false;
            if (!TryTopByte(parts[0], out int r)) return false;
            if (!TryTopByte(parts[1], out int g)) return false;
            if (!TryTopByte(parts[2], out int b)) return false;
            rgb = (uint)((r << 16) | (g << 8) | b);
            return true;
        }
        return false;
    }

    private static bool TryTopByte(string hex, out int value)
    {
        value = 0;
        if (hex.Length == 0 || hex.Length > 4) return false;
        if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int raw)) return false;
        // Scale to 8-bit by taking the top byte of the hex length.
        int scaled = hex.Length switch
        {
            1 => raw * 0x11,
            2 => raw,
            3 => (raw >> 4),
            4 => (raw >> 8),
            _ => raw,
        };
        value = scaled & 0xFF;
        return true;
    }

    private void ReplyAscii(string s) => ReplyToPty(Encoding.ASCII.GetBytes(s));

    private void SetDecMode(int[] p, bool on)
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

    private void HandleOsc8(string data)
    {
        // OSC 8 payload is "params;URL". Empty URL closes the active link.
        int semi = data.IndexOf(';');
        if (semi < 0) { _activeLinkId = 0; return; }
        string url = data.Substring(semi + 1);
        if (string.IsNullOrEmpty(url))
        {
            _activeLinkId = 0;
        }
        else
        {
            _activeLinkId = _nextHyperlinkId++;
            if (_nextHyperlinkId == 0) _nextHyperlinkId = 1;
            _hyperlinks[_activeLinkId] = url;
        }
    }

    private void EnterAlt(bool saveCursor)
    {
        if (saveCursor)
        {
            _altSavedRow = CursorRow;
            _altSavedCol = CursorCol;
            _altSavedPen = PenTemplate;
        }
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
        if (restoreCursor)
        {
            CursorRow   = Clamp(_altSavedRow, 0, Rows - 1);
            CursorCol   = Clamp(_altSavedCol, 0, Cols - 1);
            PenTemplate = _altSavedPen;
        }
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
        if (_active == _alternate)
        { _altSavedRow = CursorRow; _altSavedCol = CursorCol; _altSavedPen = PenTemplate; }
        else
        { _savedRow    = CursorRow; _savedCol    = CursorCol; _savedPen    = PenTemplate; }
    }

    private void RestoreCursor()
    {
        if (_active == _alternate)
        {
            CursorRow   = Clamp(_altSavedRow, 0, Rows - 1);
            CursorCol   = Clamp(_altSavedCol, 0, Cols - 1);
            PenTemplate = _altSavedPen;
        }
        else
        {
            CursorRow   = Clamp(_savedRow, 0, Rows - 1);
            CursorCol   = Clamp(_savedCol, 0, Cols - 1);
            PenTemplate = _savedPen;
        }
    }

    private void FullReset()
    {
        _active = _primary;
        _primary.Clear();
        _alternate.Clear();
        CursorRow = CursorCol = 0;
        PenTemplate = TerminalCell.Blank;
        CursorVisible = true;
        CursorStyle = CursorStyle.BlockBlink;
        ScrollTop = 0; ScrollBottom = Rows - 1;
        _activeG = 0; _gSlots[0] = Charset.Ascii; _gSlots[1] = Charset.Ascii;
        MouseMode = 0; BracketedPaste = false; FocusEvents = false;
        ApplicationCursorKeys = false; ApplicationKeypad = false;
        AutoWrap = true; OriginMode = false; ReverseVideo = false;
        InsertMode = false; LineFeedNewLine = false;
        ScrollOffset = 0; Selection = null;
        _activeLinkId = 0; _hyperlinks.Clear();
        _tabStops = null; // will rebuild with defaults on next access
        _lastPrintRune = 0;
        _windowTitle = string.Empty;
    }

    // ---- SGR ----

    private void ApplySgr(int[] p)
    {
        if (p.Length == 0) { PenTemplate = TerminalCell.Blank; return; }

        int i = 0;
        while (i < p.Length)
        {
            switch (p[i])
            {
                case 0:   PenTemplate = TerminalCell.Blank; break;
                case 1:   PenTemplate.Flags  |=  CellFlags.Bold;          break;
                case 2:   PenTemplate.Flags  |=  CellFlags.Dim;           break;
                case 3:   PenTemplate.Flags  |=  CellFlags.Italic;        break;
                case 4:   PenTemplate.Flags  |=  CellFlags.Underline;     break;
                case 5:
                case 6:   PenTemplate.Flags2 |=  CellFlags2.Blink;        break;
                case 7:   PenTemplate.Flags  |=  CellFlags.Inverse;       break;
                case 9:   PenTemplate.Flags  |=  CellFlags.Strikethrough; break;
                case 22:  PenTemplate.Flags  &= ~(CellFlags.Bold | CellFlags.Dim); break;
                case 23:  PenTemplate.Flags  &= ~CellFlags.Italic;        break;
                case 24:  PenTemplate.Flags  &= ~CellFlags.Underline;     break;
                case 25:  PenTemplate.Flags2 &= ~CellFlags2.Blink;        break;
                case 27:  PenTemplate.Flags  &= ~CellFlags.Inverse;       break;
                case 29:  PenTemplate.Flags  &= ~CellFlags.Strikethrough; break;

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
    private int ApplyExtColor(int[] p, int i, bool fg)
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
            if (fg) { PenTemplate.FgRgb = packed; PenTemplate.Flags |= CellFlags.FgRgb; }
            else    { PenTemplate.BgRgb = packed; PenTemplate.Flags |= CellFlags.BgRgb; }
            return 4;
        }
        return 0;
    }

    private void SetFgIdx(byte i) { PenTemplate.FgIndex = i; PenTemplate.FgRgb = 0; PenTemplate.Flags &= ~CellFlags.FgRgb; }
    private void SetBgIdx(byte i) { PenTemplate.BgIndex = i; PenTemplate.BgRgb = 0; PenTemplate.Flags &= ~CellFlags.BgRgb; }
    private void ClearFg() { PenTemplate.FgIndex = 0; PenTemplate.FgRgb = 0; PenTemplate.Flags &= ~CellFlags.FgRgb; }
    private void ClearBg() { PenTemplate.BgIndex = 0; PenTemplate.BgRgb = 0; PenTemplate.Flags &= ~CellFlags.BgRgb; }

    private TerminalCell BlankPenCell()
    {
        // Blank cells carry the current background so EL/ED with the
        // current pen paints a swath of the current bg colour.
        var c = TerminalCell.Blank;
        c.BgIndex = PenTemplate.BgIndex;
        c.BgRgb   = PenTemplate.BgRgb;
        c.Flags   = PenTemplate.Flags & CellFlags.BgRgb;
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
