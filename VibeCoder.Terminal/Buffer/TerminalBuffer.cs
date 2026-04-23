using System;
using System.Collections.Generic;
using System.Text;
using VibeCoder.Terminal.Parser;
using VibeCoder.Terminal.Render;

namespace VibeCoder.Terminal.Buffer;

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

    // Scrollback viewport. 0 = at bottom; positive = scrolled up into
    // scrollback. TerminalControl resets this to 0 on any keystroke.
    // <see cref="PixelScrollOffset"/> carries the sub-line pixel
    // remainder so the renderer can slide content smoothly — wheel
    // events accumulate in pixel space and turn over into whole-line
    // <see cref="ScrollOffset"/> bumps as they cross a line height.
    public int ScrollOffset { get; private set; }
    public double PixelScrollOffset { get; private set; }

    public TerminalSelection? Selection { get; private set; }

    // OSC 8 hyperlinks.
    private readonly Dictionary<ushort, string> _hyperlinks = new();
    private ushort _nextHyperlinkId = 1;
    private ushort _activeLinkId;

    // UTF-8 partial state for split chunks (unused now that the parser
    // handles it, but kept for future external Write(byte) callers).
    private readonly List<byte> _pendingReplies = new();

    public byte[]? TakeReplies()
    {
        if (_pendingReplies.Count == 0) return null;
        var b = _pendingReplies.ToArray();
        _pendingReplies.Clear();
        return b;
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
        if (ScrollOffset == 0) return _active.GetRow(visualRow);

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
        return screenRow < Rows ? _active.GetRow(screenRow) : null;
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

        if (CursorCol >= Cols)
        {
            CarriageReturn();
            LineFeedInternal();
        }

        // Wide glyphs need two columns; wrap if the right half would
        // spill off the line.
        if (width == 2 && CursorCol >= Cols - 1)
        {
            CarriageReturn();
            LineFeedInternal();
        }

        var row  = _active.GetRow(CursorRow);
        var cell = PenTemplate;
        cell.Rune        = rune;
        cell.HyperlinkId = _activeLinkId;

        if (width == 2)
        {
            cell.Flags2 = CellFlags2.IsWide;
            row[CursorCol] = cell;
            if (CursorCol + 1 < Cols)
            {
                var cont = PenTemplate;
                cont.Rune        = 0;
                cont.Flags2      = CellFlags2.IsContinuation;
                cont.HyperlinkId = _activeLinkId;
                row[CursorCol + 1] = cont;
            }
            CursorCol += 2;
        }
        else
        {
            cell.Flags2    = CellFlags2.None;
            row[CursorCol] = cell;
            CursorCol++;
        }
    }

    public void Execute(byte c0)
    {
        switch (c0)
        {
            case 0x07: return;                                // BEL
            case 0x08: if (CursorCol > 0) CursorCol--; return; // BS
            case 0x09: HorizontalTab(); return;
            case 0x0A: case 0x0B: case 0x0C: LineFeedInternal(); return;
            case 0x0D: CarriageReturn(); return;
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
                CursorRow = Clamp((p0 > 0 ? p0 : 1) - 1, 0, Rows - 1);
                CursorCol = Clamp((p1 > 0 ? p1 : 1) - 1, 0, Cols - 1);
                return;
            case 'J': EraseDisplay(p0); return;
            case 'K': EraseLine(p0); return;
            case 'L': _active.InsertLines(CursorRow, Max1(p0), ScrollBottom); return;
            case 'M': _active.DeleteLines(CursorRow, Max1(p0), ScrollBottom); return;
            case 'P': DeleteChars(Max1(p0)); return;
            case 'S': _active.ScrollUpRegion  (ScrollTop, ScrollBottom, Max1(p0)); return;
            case 'T': _active.ScrollDownRegion(ScrollTop, ScrollBottom, Max1(p0)); return;
            case 'X': EraseChars(Max1(p0)); return;
            case '@': InsertBlanks(Max1(p0)); return;
            case 'd': CursorRow = Clamp((p0 > 0 ? p0 : 1) - 1, 0, Rows - 1); return;
            case 'm': ApplySgr(p); return;
            case 'n': HandleDsr(p0); return;
            case 'c': ReplyToPty("\x1b[?62;4;22c"u8); return;  // VT220 DA
            case 'r': SetScrollRegion(p0, p1); return;
            case 's': SaveCursor(); return;
            case 'u': RestoreCursor(); return;
            case 'q': SetCursorStyle(p0); return;              // DECSCUSR (with/without SP intermediate)
        }
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
        switch (final)
        {
            case '7': SaveCursor(); return;                    // DECSC
            case '8': RestoreCursor(); return;                 // DECRC
            case '=': ApplicationKeypad = true;  return;       // DECKPAM
            case '>': ApplicationKeypad = false; return;       // DECKPNM
            case 'D': LineFeedInternal(); return;              // IND
            case 'E': CarriageReturn(); LineFeedInternal(); return; // NEL
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
        if (cmd == 8) HandleOsc8(data);
        // 0 / 2 (window title) — ignored for now. Wire to a TitleChanged
        // event when the tab UI wants it.
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
        // DECSTBM parks the cursor at home.
        CursorRow = 0;
        CursorCol = 0;
    }

    private void SetDecMode(int[] p, bool on)
    {
        foreach (var m in p)
        {
            switch (m)
            {
                case 1:    ApplicationCursorKeys = on; break;
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
        int next = (CursorCol + 8) & ~7;
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
            case 2:
            case 3: for (int r = 0; r < Rows; r++) ClearRow(r); break;
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
        ScrollOffset = 0; Selection = null;
        _activeLinkId = 0; _hyperlinks.Clear();
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
                case 1:   PenTemplate.Flags |=  CellFlags.Bold;          break;
                case 2:   PenTemplate.Flags |=  CellFlags.Dim;           break;
                case 3:   PenTemplate.Flags |=  CellFlags.Italic;        break;
                case 4:   PenTemplate.Flags |=  CellFlags.Underline;     break;
                case 7:   PenTemplate.Flags |=  CellFlags.Inverse;       break;
                case 9:   PenTemplate.Flags |=  CellFlags.Strikethrough; break;
                case 22:  PenTemplate.Flags &= ~(CellFlags.Bold | CellFlags.Dim); break;
                case 23:  PenTemplate.Flags &= ~CellFlags.Italic;        break;
                case 24:  PenTemplate.Flags &= ~CellFlags.Underline;     break;
                case 27:  PenTemplate.Flags &= ~CellFlags.Inverse;       break;
                case 29:  PenTemplate.Flags &= ~CellFlags.Strikethrough; break;

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
    private void Bump() { unchecked { Revision++; } }
}
