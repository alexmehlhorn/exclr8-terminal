using System;
using System.Collections.Generic;
using VibeCoder.Terminal.Parser;
using VibeCoder.Terminal.Render;

namespace VibeCoder.Terminal.Buffer;

/// <summary>
/// Authoritative terminal state: the visible cell grid, scrollback ring,
/// cursor position, current SGR attributes, and the DEC private-mode
/// flags (alt screen buffer, cursor visibility, etc.). Driven by the
/// <see cref="VtParser"/> via the <see cref="IParserActions"/> surface.
/// </summary>
public sealed class TerminalBuffer : IParserActions
{
    public int Cols { get; private set; }
    public int Rows { get; private set; }

    public int CursorRow { get; private set; }
    public int CursorCol { get; private set; }
    public bool CursorVisible { get; private set; } = true;

    /// <summary>Currently-active attributes applied to new cells.</summary>
    public TerminalCell PenTemplate = TerminalCell.Blank;

    // Primary and alternate screen buffers. Vim / htop / Claude switch
    // to the alternate buffer via DECSET 1049 to draw their full-screen
    // UI without disturbing the primary buffer's scrollback.
    private readonly ScreenBuffer _primary;
    private readonly ScreenBuffer _alternate;
    private ScreenBuffer _active;

    public int ScrollbackLimit
    {
        get => _primary.ScrollbackLimit;
        set { _primary.ScrollbackLimit = value; _alternate.ScrollbackLimit = 0; }
    }

    public int Revision { get; private set; }

    private readonly VtParser _parser;

    // Saved cursor state (DECSC / DECRC).
    private int _savedRow, _savedCol;
    private TerminalCell _savedPen;

    // Scroll region (DECSTBM). Inclusive. Defaults to whole screen.
    public int ScrollTop { get; private set; }
    public int ScrollBottom { get; private set; }

    // Character set slots G0/G1 (SCS — ESC ( X / ESC ) X). Most terminals
    // only support DEC Special Graphics (0) and US ASCII (B). SI/SO (0x0F/
    // 0x0E) select the active slot.
    public enum Charset { Ascii, DecSpecialGraphics }
    private readonly Charset[] _gSlots = new Charset[2] { Charset.Ascii, Charset.Ascii };
    private int _activeG;

    // Modes.
    public bool BracketedPaste { get; private set; }
    public bool ApplicationCursorKeys { get; private set; }
    public bool ApplicationKeypad { get; private set; }

    // DSR/DA replies queued during a parse. WindowControl reads and
    // flushes these back to the PTY via the Output event.
    private readonly List<byte> _pendingReplies = new();

    /// <summary>Replies accumulated during the last <see cref="Write"/>;
    /// <see cref="TerminalControl"/> forwards them to the PTY.</summary>
    public byte[]? TakeReplies()
    {
        if (_pendingReplies.Count == 0) return null;
        var bytes = _pendingReplies.ToArray();
        _pendingReplies.Clear();
        return bytes;
    }

    public TerminalBuffer(int cols, int rows)
    {
        if (cols < 1) cols = 80;
        if (rows < 1) rows = 24;
        Cols = cols;
        Rows = rows;
        _primary = new ScreenBuffer(cols, rows, scrollbackLimit: 5000);
        _alternate = new ScreenBuffer(cols, rows, scrollbackLimit: 0);
        _active = _primary;
        _parser = new VtParser(this);
    }

    public TerminalCell[] GetVisibleRow(int r) => _active.GetRow(r);

    public IEnumerable<TerminalCell[]> AllRows()
    {
        foreach (var r in _active.Scrollback) yield return r;
        for (int i = 0; i < Rows; i++) yield return _active.GetRow(i);
    }

    public int ScrollbackCount => _active.Scrollback.Count;

    public void Resize(int cols, int rows)
    {
        if (cols < 1 || rows < 1 || (cols == Cols && rows == Rows)) return;

        _primary.Resize(cols, rows);
        _alternate.Resize(cols, rows);
        Cols = cols;
        Rows = rows;

        if (CursorCol >= cols) CursorCol = cols - 1;
        if (CursorRow >= rows) CursorRow = rows - 1;

        Bump();
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        _parser.Parse(bytes);
        Bump();
    }

    // ------------------------------------------------------------------
    // IParserActions
    // ------------------------------------------------------------------

    public void Print(int rune)
    {
        if (rune == 0) return;

        if (CursorCol >= Cols)
        {
            // Autowrap.
            CarriageReturn();
            LineFeedInternal();
        }

        var row = _active.GetRow(CursorRow);
        var cell = PenTemplate;
        cell.Rune = rune;
        row[CursorCol] = cell;
        CursorCol++;
    }

    public void Execute(byte c0)
    {
        switch (c0)
        {
            case 0x07: /* BEL */ return;
            case 0x08: Backspace(); return;
            case 0x09: HorizontalTab(); return;
            case 0x0A: /* LF */ LineFeedInternal(); return;
            case 0x0B: /* VT */ LineFeedInternal(); return;
            case 0x0C: /* FF */ LineFeedInternal(); return;
            case 0x0D: CarriageReturn(); return;
            // Others (NUL, SI, SO, etc.) — ignored for now.
        }
    }

    public void CsiDispatch(char final, int[] parameters, string intermediates, char privatePrefix)
    {
        int p0 = parameters.Length > 0 ? parameters[0] : 0;
        int p1 = parameters.Length > 1 ? parameters[1] : 0;

        if (privatePrefix == '?')
        {
            // DEC private modes — handle the ones that matter.
            switch (final)
            {
                case 'h': SetDecPrivateMode(parameters, true); return;
                case 'l': SetDecPrivateMode(parameters, false); return;
            }
            return;
        }

        switch (final)
        {
            case 'A': MoveCursor(0, -Max1(p0)); return;                  // CUU
            case 'B': MoveCursor(0, +Max1(p0)); return;                  // CUD
            case 'C': MoveCursor(+Max1(p0), 0); return;                  // CUF
            case 'D': MoveCursor(-Max1(p0), 0); return;                  // CUB
            case 'E': CursorCol = 0; MoveCursor(0, +Max1(p0)); return;   // CNL
            case 'F': CursorCol = 0; MoveCursor(0, -Max1(p0)); return;   // CPL
            case 'G': CursorCol = Clamp((p0 > 0 ? p0 : 1) - 1, 0, Cols - 1); return; // CHA
            case 'H':
            case 'f':
            {
                int row = parameters.Length > 0 && parameters[0] > 0 ? parameters[0] : 1;
                int col = parameters.Length > 1 && parameters[1] > 0 ? parameters[1] : 1;
                CursorRow = Clamp(row - 1, 0, Rows - 1);
                CursorCol = Clamp(col - 1, 0, Cols - 1);
                return;
            }
            case 'J': EraseDisplay(p0); return;
            case 'K': EraseLine(p0); return;
            case 'S': ScrollUp(Max1(p0)); return;
            case 'T': ScrollDown(Max1(p0)); return;
            case 'd': CursorRow = Clamp((p0 > 0 ? p0 : 1) - 1, 0, Rows - 1); return; // VPA
            case 'm': ApplySgr(parameters); return;
            case 'n':
                // DSR — device status report. Claude / zsh sometimes issue
                // these; we no-op for now (would need a reply channel to
                // answer properly).
                return;
            case 'r':
                // DECSTBM — scroll region. Not implemented yet; vim / less
                // will render slightly wrong without it (partial scrolls).
                return;
            case 's': DecSaveCursor(); return;
            case 'u': DecRestoreCursor(); return;
            case 'X': EraseChars(Max1(p0)); return;
            case 'P': DeleteChars(Max1(p0)); return;
            case '@': InsertBlanks(Max1(p0)); return;
            case 'L': InsertLines(Max1(p0)); return;
            case 'M': DeleteLines(Max1(p0)); return;
        }
    }

    public void EscDispatch(char final, string intermediates)
    {
        switch (final)
        {
            case '7': DecSaveCursor(); return;   // DECSC
            case '8': DecRestoreCursor(); return; // DECRC
            case 'D': LineFeedInternal(); return; // IND
            case 'E': CarriageReturn(); LineFeedInternal(); return; // NEL
            case 'M': ReverseIndex(); return;    // RI — cursor up with scroll
            case 'c': FullReset(); return;       // RIS
        }
    }

    public void OscDispatch(string payload)
    {
        // Accept OSC 0 / OSC 2 (window title) but no-op them for now —
        // hook up to Tab/Cell display later.
        // Accept OSC 8 (hyperlinks), ignored. Claude doesn't use these.
    }

    public void ReplyToPty(ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++) _pendingReplies.Add(bytes[i]);
    }

    // ------------------------------------------------------------------
    // Implementation helpers
    // ------------------------------------------------------------------

    private void SetDecPrivateMode(int[] parameters, bool enable)
    {
        foreach (var p in parameters)
        {
            switch (p)
            {
                case 25: CursorVisible = enable; break;
                case 47:   // Use alternate screen buffer (legacy)
                case 1047: // Use alternate screen buffer
                case 1049: // Save cursor + alt buffer + clear
                    if (enable) EnterAlternateBuffer(saveCursor: p == 1049);
                    else LeaveAlternateBuffer(restoreCursor: p == 1049);
                    break;
                // 1000, 1002, 1006 etc. — mouse modes, not implemented yet.
                // 2004 — bracketed paste, not implemented yet.
            }
        }
    }

    private void EnterAlternateBuffer(bool saveCursor)
    {
        if (saveCursor) DecSaveCursor();
        if (_active == _alternate) return;
        _active = _alternate;
        _active.Clear();
    }

    private void LeaveAlternateBuffer(bool restoreCursor)
    {
        if (_active != _alternate) return;
        _active = _primary;
        if (restoreCursor) DecRestoreCursor();
    }

    private void CarriageReturn() => CursorCol = 0;

    private void LineFeedInternal()
    {
        if (CursorRow < Rows - 1) { CursorRow++; return; }
        _active.ScrollUp();
    }

    private void Backspace() { if (CursorCol > 0) CursorCol--; }

    private void HorizontalTab()
    {
        int next = (CursorCol + 8) & ~7;
        CursorCol = Math.Min(next, Cols - 1);
    }

    private void ReverseIndex()
    {
        if (CursorRow > 0) { CursorRow--; return; }
        _active.ScrollDown();
    }

    private void MoveCursor(int dx, int dy)
    {
        CursorCol = Clamp(CursorCol + dx, 0, Cols - 1);
        CursorRow = Clamp(CursorRow + dy, 0, Rows - 1);
    }

    private void EraseDisplay(int mode)
    {
        // 0: cursor-to-end, 1: start-to-cursor, 2: whole screen
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (int r = CursorRow + 1; r < Rows; r++) ClearRow(r);
                break;
            case 1:
                EraseLine(1);
                for (int r = 0; r < CursorRow; r++) ClearRow(r);
                break;
            case 2:
            case 3:
                for (int r = 0; r < Rows; r++) ClearRow(r);
                break;
        }
    }

    private void EraseLine(int mode)
    {
        var row = _active.GetRow(CursorRow);
        switch (mode)
        {
            case 0: for (int c = CursorCol; c < Cols; c++) row[c] = BlankPenCell(); break;
            case 1: for (int c = 0; c <= CursorCol && c < Cols; c++) row[c] = BlankPenCell(); break;
            case 2: for (int c = 0; c < Cols; c++) row[c] = BlankPenCell(); break;
        }
    }

    private void ClearRow(int r)
    {
        var row = _active.GetRow(r);
        for (int c = 0; c < Cols; c++) row[c] = BlankPenCell();
    }

    private void ScrollUp(int n) { for (int i = 0; i < n; i++) _active.ScrollUp(); }
    private void ScrollDown(int n) { for (int i = 0; i < n; i++) _active.ScrollDown(); }

    private void EraseChars(int n)
    {
        var row = _active.GetRow(CursorRow);
        for (int i = 0; i < n && CursorCol + i < Cols; i++) row[CursorCol + i] = BlankPenCell();
    }

    private void DeleteChars(int n)
    {
        var row = _active.GetRow(CursorRow);
        int src = CursorCol + n;
        int dst = CursorCol;
        while (src < Cols) row[dst++] = row[src++];
        while (dst < Cols) row[dst++] = BlankPenCell();
    }

    private void InsertBlanks(int n)
    {
        var row = _active.GetRow(CursorRow);
        for (int c = Cols - 1; c >= CursorCol + n; c--) row[c] = row[c - n];
        for (int c = CursorCol; c < CursorCol + n && c < Cols; c++) row[c] = BlankPenCell();
    }

    private void InsertLines(int n) => _active.InsertLines(CursorRow, n);
    private void DeleteLines(int n) => _active.DeleteLines(CursorRow, n);

    private void DecSaveCursor()
    {
        _savedRow = CursorRow;
        _savedCol = CursorCol;
        _savedPen = PenTemplate;
    }

    private void DecRestoreCursor()
    {
        CursorRow = Clamp(_savedRow, 0, Rows - 1);
        CursorCol = Clamp(_savedCol, 0, Cols - 1);
        PenTemplate = _savedPen;
    }

    private void FullReset()
    {
        _active = _primary;
        _primary.Clear();
        _alternate.Clear();
        CursorRow = CursorCol = 0;
        PenTemplate = TerminalCell.Blank;
        CursorVisible = true;
    }

    // ------------------------------------------------------------------
    // SGR (Select Graphic Rendition) — ESC [ ... m
    // ------------------------------------------------------------------

    private void ApplySgr(int[] parameters)
    {
        int i = 0;
        if (parameters.Length == 0)
        {
            PenTemplate = TerminalCell.Blank;
            return;
        }
        while (i < parameters.Length)
        {
            int p = parameters[i];
            switch (p)
            {
                case 0:
                    PenTemplate = TerminalCell.Blank;
                    break;
                case 1:  PenTemplate.Flags |= CellFlags.Bold;      break;
                case 2:  PenTemplate.Flags |= CellFlags.Dim;       break;
                case 3:  PenTemplate.Flags |= CellFlags.Italic;    break;
                case 4:  PenTemplate.Flags |= CellFlags.Underline; break;
                case 7:  PenTemplate.Flags |= CellFlags.Inverse;   break;
                case 22: PenTemplate.Flags &= ~(CellFlags.Bold | CellFlags.Dim); break;
                case 23: PenTemplate.Flags &= ~CellFlags.Italic;   break;
                case 24: PenTemplate.Flags &= ~CellFlags.Underline; break;
                case 27: PenTemplate.Flags &= ~CellFlags.Inverse;  break;

                case 30: case 31: case 32: case 33:
                case 34: case 35: case 36: case 37:
                    SetFgIndex((byte)(p - 30)); break;
                case 39: ClearFg(); break;

                case 40: case 41: case 42: case 43:
                case 44: case 45: case 46: case 47:
                    SetBgIndex((byte)(p - 40)); break;
                case 49: ClearBg(); break;

                case 90: case 91: case 92: case 93:
                case 94: case 95: case 96: case 97:
                    SetFgIndex((byte)(p - 90 + 8)); break;
                case 100: case 101: case 102: case 103:
                case 104: case 105: case 106: case 107:
                    SetBgIndex((byte)(p - 100 + 8)); break;

                case 38: i += ApplyExtendedColor(parameters, i, foreground: true); break;
                case 48: i += ApplyExtendedColor(parameters, i, foreground: false); break;
            }
            i++;
        }
    }

    // Handles 256-color (ESC[38;5;N m) and 24-bit (ESC[38;2;R;G;B m).
    private int ApplyExtendedColor(int[] p, int i, bool foreground)
    {
        if (i + 1 >= p.Length) return 0;
        int kind = p[i + 1];
        if (kind == 5 && i + 2 < p.Length)
        {
            byte idx = (byte)(p[i + 2] & 0xFF);
            if (foreground) SetFgIndex(idx); else SetBgIndex(idx);
            return 2;
        }
        if (kind == 2 && i + 4 < p.Length)
        {
            byte r = (byte)(p[i + 2] & 0xFF);
            byte g = (byte)(p[i + 3] & 0xFF);
            byte b = (byte)(p[i + 4] & 0xFF);
            uint packed = (uint)((r << 16) | (g << 8) | b);
            if (foreground)
            {
                PenTemplate.FgRgb = packed;
                PenTemplate.Flags |= CellFlags.FgRgb;
            }
            else
            {
                PenTemplate.BgRgb = packed;
                PenTemplate.Flags |= CellFlags.BgRgb;
            }
            return 4;
        }
        return 0;
    }

    private void SetFgIndex(byte idx)
    {
        PenTemplate.FgIndex = idx;
        PenTemplate.FgRgb = 0;
        PenTemplate.Flags &= ~CellFlags.FgRgb;
    }

    private void SetBgIndex(byte idx)
    {
        PenTemplate.BgIndex = idx;
        PenTemplate.BgRgb = 0;
        PenTemplate.Flags &= ~CellFlags.BgRgb;
    }

    private void ClearFg()
    {
        PenTemplate.FgIndex = 0;
        PenTemplate.FgRgb = 0;
        PenTemplate.Flags &= ~CellFlags.FgRgb;
    }

    private void ClearBg()
    {
        PenTemplate.BgIndex = 0;
        PenTemplate.BgRgb = 0;
        PenTemplate.Flags &= ~CellFlags.BgRgb;
    }

    private TerminalCell BlankPenCell()
    {
        // A blank cell inherits the current bg (so e.g. ED with reversed
        // pen paints a colored swath), but not the fg/glyph.
        var c = TerminalCell.Blank;
        c.BgIndex = PenTemplate.BgIndex;
        c.BgRgb = PenTemplate.BgRgb;
        c.Flags = PenTemplate.Flags & CellFlags.BgRgb;
        return c;
    }

    private static int Max1(int n) => n > 0 ? n : 1;
    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    private void Bump() { unchecked { Revision++; } }
}
