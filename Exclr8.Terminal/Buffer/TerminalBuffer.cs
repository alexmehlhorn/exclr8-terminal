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
    private readonly record struct SavedCursor
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

    /// <summary>True when a <see cref="Print"/> has laid down at least
    /// one cell with the <see cref="CellFlags2.Blink"/> bit since the
    /// last reset. Lets the host skip 2 Hz blink-timer repaints when
    /// nothing on the grid actually blinks — the common case. Cleared
    /// on <see cref="FullReset"/> / <see cref="SoftReset"/>; once set,
    /// stays set until reset (we don't walk the grid to find out when
    /// the last Blink cell scrolls off).</summary>
    public bool HasBlinkContent { get; private set; }

    public enum Charset { Ascii, DecSpecialGraphics }
    private readonly Charset[] _gSlots = { Charset.Ascii, Charset.Ascii };
    private int _activeG; // 0 = G0, 1 = G1

    // DEC private modes.
    public bool BracketedPaste        { get; private set; }
    public bool ApplicationCursorKeys { get; private set; }
    public bool ApplicationKeypad     { get; private set; }
    public int  MouseMode             { get; private set; } // 0 / 9 (X10) / 1000 / 1002 / 1003
    /// <summary>Mouse-event encoding the app last requested via DECSET
    /// 1006 (SGR) / 1016 (SGR pixel). Default = legacy X10 encoding
    /// (CSI M Cb Cx Cy with the 32-byte offset, capped at column 223).
    /// SGR is the only encoding modern apps care about; pixel form is
    /// the same protocol but coordinates are pixels not cells. Set when
    /// the corresponding DECSET arrives, cleared on RIS / DECSTR.</summary>
    public MouseEncoding MouseEncoding { get; private set; } = MouseEncoding.Default;
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
    /// <summary>DECSET 45 — reverse wraparound. When on, BS at column 0
    /// wraps the cursor to the end of the previous line. Off by default;
    /// rarely used outside of readline-style line editing.</summary>
    public bool ReverseWraparound     { get; private set; }
    /// <summary>xterm modifyOtherKeys level (XTMODKEYS &gt; 4 ; level m).
    /// 0 = legacy (Ctrl+letter as 0x01..0x1A, Alt+key as ESC+key);
    /// 1 = legacy with the modifier-parameter form for special keys;
    /// 2 = full disambiguation — every Ctrl/Shift/Alt+key combination
    /// reports as <c>CSI 27;mod;keycode~</c>. Apps like Neovim and
    /// helix request level 2 on startup so Ctrl+Shift+letter doesn't
    /// collide with plain Ctrl+letter.</summary>
    public int ModifyOtherKeys      { get; private set; }

    /// <summary>DECSET 2026 — synchronized output. When on, the host
    /// renderer should buffer drawing changes and flush them as one
    /// frame to prevent tearing. Subscribers can read this to coalesce
    /// invalidations; the buffer raises <see cref="SynchronizedOutputChanged"/>
    /// when it flips so the renderer can react.</summary>
    public bool SynchronizedOutput
    {
        get => _synchronizedOutput;
        private set
        {
            if (_synchronizedOutput == value) return;
            _synchronizedOutput = value;
            SynchronizedOutputChanged?.Invoke(this, value);
        }
    }
    private bool _synchronizedOutput;
    public event EventHandler<bool>? SynchronizedOutputChanged;

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

    /// <summary>True when the shell explicitly set a default
    /// foreground via OSC 10. Renderers prefer the host theme over
    /// the buffer's pre-seeded value until this is true.</summary>
    public bool DefaultForegroundExplicit => _osc.DefaultForegroundExplicit;
    public bool DefaultBackgroundExplicit => _osc.DefaultBackgroundExplicit;
    public bool DefaultCursorExplicit     => _osc.DefaultCursorExplicit;

    /// <summary>Palette override for index <paramref name="idx"/>
    /// (0..255) set by the shell via OSC 4. Returns true when an
    /// override exists; the renderer falls back to host theme +
    /// static palette otherwise.</summary>
    public bool TryGetDynamicPaletteColor(int idx, out uint rgb) =>
        _osc.TryGetPaletteColor(idx, out rgb);

    /// <summary>OSC 4 / 10 / 11 / 12 changed the live palette or a
    /// default colour. Renderer hooks this to invalidate visuals.</summary>
    public event EventHandler? PaletteChanged
    {
        add    => _osc.PaletteChanged += value;
        remove => _osc.PaletteChanged -= value;
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

    // Parser extensibility: lists of user-registered handlers keyed on
    // identifier. Iterated in reverse-registration order on dispatch
    // (most-recent wins, matches xterm.js semantics) and short-circuited
    // when any handler returns true.
    private readonly Dictionary<(char Final, char Prefix), List<CsiHandler>> _csiHandlers = new();
    private readonly Dictionary<int,  List<OscHandler>> _oscHandlers = new();
    private readonly Dictionary<(char Final, string Intermediates), List<EscHandler>> _escHandlers = new();
    private readonly Dictionary<(char Final, string Intermediates), List<DcsHandler>> _dcsHandlers = new();

    /// <summary>Register a custom CSI handler. Returns a disposable
    /// that detaches the handler. Most-recent registration wins —
    /// returning <c>false</c> falls through to the next handler and
    /// ultimately to the built-in path. Use this to implement custom
    /// CSI extensions (e.g. progress reports, vendor sequences) without
    /// forking the buffer.</summary>
    public IDisposable RegisterCsiHandler(char final, char privatePrefix, CsiHandler handler)
        => RegisterIn(_csiHandlers, (final, privatePrefix), handler);

    /// <summary>Register a custom OSC handler keyed on the identifier
    /// integer (e.g. 7, 133, 1337). Same fall-through semantics as
    /// CSI.</summary>
    public IDisposable RegisterOscHandler(int identifier, OscHandler handler)
        => RegisterIn(_oscHandlers, identifier, handler);

    /// <summary>Register a custom ESC handler — sequences of the form
    /// <c>ESC &lt;intermediates&gt; &lt;final&gt;</c>. Pass empty string
    /// for the no-intermediate form (e.g. <c>ESC c</c>).</summary>
    public IDisposable RegisterEscHandler(char final, string intermediates, EscHandler handler)
        => RegisterIn(_escHandlers, (final, intermediates), handler);

    /// <summary>Register a custom DCS handler — host-side hook for
    /// sixel, kitty graphics, DECRQSS, etc.</summary>
    public IDisposable RegisterDcsHandler(char final, string intermediates, DcsHandler handler)
        => RegisterIn(_dcsHandlers, (final, intermediates), handler);

    private static IDisposable RegisterIn<TKey, THandler>(
        Dictionary<TKey, List<THandler>> map, TKey key, THandler handler) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<THandler>();
            map[key] = list;
        }
        list.Add(handler);
        return new HandlerRegistration(() =>
        {
            if (map.TryGetValue(key, out var l))
            {
                l.Remove(handler);
                if (l.Count == 0) map.Remove(key);
            }
        });
    }

    private sealed class HandlerRegistration : IDisposable
    {
        private Action? _dispose;
        public HandlerRegistration(Action dispose) { _dispose = dispose; }
        public void Dispose()
        {
            var d = _dispose; _dispose = null;
            d?.Invoke();
        }
    }

    // ---- Markers / Decorations ----

    private readonly List<TerminalMarker> _markers = new();
    private readonly List<TerminalDecoration> _decorations = new();

    /// <summary>Total number of scrollback rows that have been evicted
    /// over the buffer's lifetime. Markers use this as a stable
    /// reference when their anchored content scrolls off.</summary>
    public long ScrollbackEvictions => _primary.Scrollback.EvictionCount;

    /// <summary>All registered decorations. Renderer walks this on every
    /// frame to paint them at their markers' current visible rows.</summary>
    public IReadOnlyList<TerminalDecoration> Decorations => _decorations;

    /// <summary>Anchor a marker to <paramref name="cursorYOffset"/> rows
    /// from the current cursor row (0 = current row, -1 = one row up,
    /// etc.). Returns a <see cref="TerminalMarker"/> that survives
    /// scroll-into-scrollback. Caller owns disposal.</summary>
    public TerminalMarker RegisterMarker(int cursorYOffset = 0)
    {
        int visualRow = Math.Clamp(CursorRow + cursorYOffset, 0, Rows - 1);
        int abs = _active.Scrollback.Count + visualRow;
        var marker = new TerminalMarker(this, abs);
        _markers.Add(marker);
        return marker;
    }

    /// <summary>Register a decoration anchored to an existing marker.
    /// The decoration is drawn until either it or its marker is
    /// disposed.</summary>
    public TerminalDecoration RegisterDecoration(DecorationOptions options)
    {
        if (options.Marker == null)
            throw new ArgumentException("Decoration must reference a marker.", nameof(options));
        if (options.Marker.IsDisposed)
            throw new ArgumentException("Marker is already disposed.", nameof(options));
        var dec = new TerminalDecoration(this, options);
        _decorations.Add(dec);
        // Race: the marker may have disposed between the
        // IsDisposed check above and the constructor's
        // Marker.Disposed subscription, OR between that subscription
        // and the Add above. In the first case Disposed is one-shot
        // and our late subscription never fires. In the second the
        // handler ran but RemoveDecoration was a no-op because we
        // weren't in _decorations yet. Either way the orphan is in
        // _decorations now — clean up by re-checking.
        if (dec.Marker.IsDisposed)
        {
            dec.Dispose();
            throw new ArgumentException("Marker is already disposed.", nameof(options));
        }
        Bump();
        return dec;
    }

    internal void RemoveMarker(TerminalMarker m)
    {
        _markers.Remove(m);
        Bump();
    }
    internal void RemoveDecoration(TerminalDecoration d)
    {
        _decorations.Remove(d);
        Bump();
    }

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

    /// <summary>Working directory most recently announced via OSC 7.
    /// Null until the shell emits one. Hosts use this for "open new
    /// tab here" UX and session recall.</summary>
    public string? WorkingDirectory => _osc.WorkingDirectory;

    /// <summary>Fired when OSC 7 announces a new working directory.</summary>
    public event EventHandler<string>? WorkingDirectoryChanged
    {
        add    => _osc.WorkingDirectoryChanged += value;
        remove => _osc.WorkingDirectoryChanged -= value;
    }

    /// <summary>FinalTerm/iTerm2 semantic-prompt markers (OSC 133).
    /// Hosts subscribe to draw command-status gutters, jump-to-prompt
    /// nav, and AI-style command boundaries.</summary>
    public event EventHandler<SemanticPromptEventArgs>? SemanticPrompt
    {
        add    => _osc.SemanticPrompt += value;
        remove => _osc.SemanticPrompt -= value;
    }

    /// <summary>ConEmu / Windows-Terminal task-progress notifications
    /// (OSC 9 ; 4 ; state ; pct). Hosts surface as taskbar overlays
    /// or dock badges.</summary>
    public event EventHandler<ProgressEventArgs>? ProgressChanged
    {
        add    => _osc.ProgressChanged += value;
        remove => _osc.ProgressChanged -= value;
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

        // Track the row of the most recent OSC 133 PromptStart so
        // ClearScreenAndScrollback can preserve the user's prompt
        // (and any in-progress wrapped input) when they hit Cmd+K.
        // Markers handle scrollback-eviction tracking for free; on
        // resize the marker auto-invalidates and we fall back to
        // "preserve cursor row only".
        _osc.SemanticPrompt += OnInternalSemanticPrompt;

        // OSC 8 hyperlink ids are ushort. After 65535 distinct emissions
        // they wrap; before the dispatcher recycles the id space we
        // walk every cell and zero any HyperlinkId so on-screen cells
        // cannot resolve to a recycled slot.
        _osc.HyperlinkIdsRecycled += OnHyperlinkIdsRecycled;
    }

    private void OnHyperlinkIdsRecycled()
    {
        ZeroHyperlinkIds(_primary);
        ZeroHyperlinkIds(_alternate);
    }

    private static void ZeroHyperlinkIds(ScreenBuffer screen)
    {
        for (int r = 0; r < screen.Rows; r++)
        {
            var row = screen.GetRow(r);
            for (int c = 0; c < row.Length; c++)
                if (row[c].HyperlinkId != 0) row[c].HyperlinkId = 0;
        }
        foreach (var row in screen.Scrollback)
        {
            if (row == null) continue;
            for (int c = 0; c < row.Length; c++)
                if (row[c].HyperlinkId != 0) row[c].HyperlinkId = 0;
        }
    }

    private TerminalMarker? _promptStartMarker;

    private void OnInternalSemanticPrompt(object? sender, SemanticPromptEventArgs e)
    {
        if (e.Kind != SemanticPromptKind.PromptStart) return;
        _promptStartMarker?.Dispose();
        _promptStartMarker = RegisterMarker(); // anchored to current cursor row
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
        // Reflow lives in ScreenBuffer.Resize. Each screen needs to
        // know its OWN cursor — using (0,0) for the inactive screen
        // makes ScreenBuffer.Resize drop bottom rows on shrink instead
        // of evicting top rows into scrollback, which silently throws
        // away the prompt content sitting near the bottom of a
        // backgrounded primary while the user is in vim/htop. We use
        // the saved-cursor slot for whichever screen isn't active —
        // it was set on alt-screen entry (DECSET 1049) or by the most
        // recent DECSC, and it's the best signal we have about where
        // the inactive screen's "live" cursor would resume.
        int primaryRow = _active == _primary ? CursorRow : _primarySaved.Row;
        int primaryCol = _active == _primary ? CursorCol : _primarySaved.Col;
        int altRow     = _active == _alternate ? CursorRow : _alternateSaved.Row;
        int altCol     = _active == _alternate ? CursorCol : _alternateSaved.Col;

        var (newPRow, newPCol) = _primary.Resize(cols, rows, primaryRow, primaryCol);
        var (newARow, newACol) = _alternate.Resize(cols, rows, altRow, altCol);

        Cols = cols; Rows = rows;
        CursorRow = _active == _primary ? newPRow : newARow;
        CursorCol = _active == _primary ? newPCol : newACol;
        // Roll the reflowed cursor of the inactive screen back into
        // its saved slot so a future DECRC / 1049-leave restores to a
        // sensible location instead of the pre-reflow row index.
        if (_active != _primary)
            _primarySaved = _primarySaved with { Row = newPRow, Col = newPCol };
        if (_active != _alternate)
            _alternateSaved = _alternateSaved with { Row = newARow, Col = newACol };

        ScrollTop    = 0;
        ScrollBottom = rows - 1;
        _viewport.Reset(); // viewport must follow new bottom
        _tabStops = null;  // rebuild with new column count
        // Reflow shifted absolute row indices: a Selection or in-flight
        // search match that pointed at row N before the resize now
        // points at unrelated content. Rather than guess at remapping
        // (the cells that backed the selection may have moved across
        // multiple reflowed rows), drop both — the host can re-issue
        // a search on the new buffer if needed.
        if (Selection != null) SetSelection(null);
        _search.Clear();
        Bump();
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        int prevRow = CursorRow, prevCol = CursorCol;
        _parser.Parse(bytes);
        if (prevRow != CursorRow || prevCol != CursorCol)
            CursorMoved?.Invoke(this, (CursorRow, CursorCol));
        Bump();
    }

    /// <summary>Cursor position changed during a Write. Fires once per
    /// Write call, AFTER all parsing — not once per cursor mutation —
    /// so high-rate updates (ANSI animation) don't flood subscribers.
    /// </summary>
    public event EventHandler<(int Row, int Col)>? CursorMoved;

    /// <summary>Scroll offset changed (user scrolled scrollback or live
    /// content reset the offset). Fires once per change.</summary>
    public event EventHandler<int>? ScrollChanged;

    /// <summary>Selection state changed — set, extended, cleared.
    /// New value is null when cleared. Hosts use this to drive a
    /// "selection active" UI affordance.</summary>
    public event EventHandler<TerminalSelection?>? SelectionChanged;

    // ---- Scrollback viewport ----

    public void SetScrollOffset(int offset)
    {
        if (_viewport.SetOffset(offset, _active.Scrollback.Count))
        {
            ScrollChanged?.Invoke(this, _viewport.Offset);
            Bump();
        }
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
        if (_viewport.AddPixels(pixels, lineHeight, _active.Scrollback.Count))
        {
            ScrollChanged?.Invoke(this, _viewport.Offset);
            Bump();
        }
    }

    public void ResetScrollOffset()
    {
        if (_viewport.Reset())
        {
            ScrollChanged?.Invoke(this, _viewport.Offset);
            Bump();
        }
    }

    /// <summary>Discard the scrollback buffer entirely. Live screen
    /// is preserved — this only drops the rows that have already
    /// scrolled off the top. Snaps the view to the live screen.
    /// Use <see cref="ClearScreenAndScrollback"/> for the
    /// "Cmd+K-equivalent" behaviour where the user wants the entire
    /// terminal blank.</summary>
    public void ClearScrollback()
    {
        _active.ClearScrollback();
        _viewport.Reset();
        Bump();
    }

    /// <summary>"Clean up my screen" — wipe the noise above the
    /// prompt, drop the scrollback, snap the prompt to the top, but
    /// keep the user's in-progress input visible and usable. Wired
    /// to Cmd+K / Ctrl+Shift+K.
    ///
    /// <para>Behaviour by case:</para>
    /// <list type="bullet">
    ///   <item><b>Alt-screen active:</b> no-op. The TUI owns the
    ///         display; wiping it would vandalise vim/htop/less.</item>
    ///   <item><b>OSC 133 prompt-tracking active:</b> preserve the
    ///         row range from the most recent <c>PromptStart</c>
    ///         marker through the current cursor row. Snaps that
    ///         block to the top, blanks everything else, drops
    ///         scrollback. Multi-line prompts and wrapped in-progress
    ///         input survive.</item>
    ///   <item><b>No OSC 133 marker (or it's been invalidated by a
    ///         resize / scrollback eviction):</b> preserve just the
    ///         cursor's row. Single-line prompt survives;
    ///         multi-line decoration is lost but redraws on next
    ///         interaction.</item>
    /// </list>
    /// <para>SGR pen, DEC modes, OSC 8 hyperlink table, palette
    /// overrides — all preserved (use <see cref="ResetTerminal"/>
    /// for the heavier RIS). The shell doesn't know we did this, so
    /// any decorations / multi-line prompt parts that got wiped
    /// will redraw on the next prompt cycle.</para>
    /// </summary>
    public void ClearScreenAndScrollback()
    {
        // Alt-screen TUIs own their painted state; wiping it would
        // confuse the app. Cmd+K is meaningfully a no-op while in
        // vim / htop / claude-code-on-alt-screen.
        if (IsAltScreen) return;

        // Determine the row range to preserve: [startRow, CursorRow]
        // inclusive. Default to "just the cursor row" if no prompt
        // marker is tracked or the marker is no longer convertible
        // to a valid live-screen row.
        int startRow = CursorRow;
        if (_promptStartMarker != null && _promptStartMarker.IsValid)
        {
            int absRow = _promptStartMarker.Line;
            int sbCount = _active.Scrollback.Count;
            int visualRow = absRow - sbCount;
            if (visualRow >= 0 && visualRow < Rows && visualRow <= CursorRow)
                startRow = visualRow;
        }
        int preserveCount = CursorRow - startRow + 1;

        // Snapshot the rows we want to keep BEFORE blanking — they
        // live in the same _rows list we're about to wipe. Deep copy
        // each row so we own the cell data independent of the
        // live-screen array slots.
        var snap = new TerminalCell[preserveCount][];
        for (int i = 0; i < preserveCount; i++)
        {
            var src = _active.GetRow(startRow + i);
            snap[i] = (TerminalCell[])src.Clone();
        }

        // Blank every live row + drop scrollback.
        for (int r = 0; r < Rows; r++)
        {
            var row = _active.GetRow(r);
            for (int c = 0; c < Cols; c++) row[c] = TerminalCell.Blank;
            _active.SetWrapped(r, false);
        }
        _active.ClearScrollback();

        // Restore the preserved block at the top.
        for (int i = 0; i < preserveCount; i++)
        {
            var dst = _active.GetRow(i);
            int n = Math.Min(snap[i].Length, dst.Length);
            Array.Copy(snap[i], dst, n);
        }

        // Cursor moves to its position within the preserved block.
        CursorRow = preserveCount - 1;
        // CursorCol stays where it was — column unchanged.
        _viewport.Reset();

        // Re-anchor the prompt marker to the new top if we used it,
        // otherwise drop it. Scrollback-eviction bumped via Clear()
        // already invalidated the old marker.
        _promptStartMarker?.Dispose();
        _promptStartMarker = (preserveCount > 1) ? RegisterMarker(-(preserveCount - 1)) : null;

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
        SetSelection(new TerminalSelection(abs, col, abs, col, SelectionMode.Character));
    }

    public void ExtendSelection(int row, int col)
    {
        if (Selection == null) return;
        int abs = VisualToAbsRow(row);
        SetSelection(Selection with { EndRow = abs, EndCol = col });
    }

    public void ClearSelection()
    {
        if (Selection != null) SetSelection(null);
    }

    public void SelectWord(int row, int col)
    {
        var cells = GetRowForRender(row);
        if (cells == null) return;
        int s = col, e = col;
        while (s > 0           && IsWordChar(cells[s - 1])) s--;
        while (e < Cols - 1    && IsWordChar(cells[e + 1])) e++;
        int abs = VisualToAbsRow(row);
        SetSelection(new TerminalSelection(abs, s, abs, e, SelectionMode.Word));
    }

    public void SelectLine(int row)
    {
        int abs = VisualToAbsRow(row);
        SetSelection(new TerminalSelection(abs, 0, abs, Cols - 1, SelectionMode.Line));
    }

    /// <summary>Centralised setter so every selection mutation fires
    /// SelectionChanged exactly once and bumps the revision.</summary>
    private void SetSelection(TerminalSelection? sel)
    {
        Selection = sel;
        SelectionChanged?.Invoke(this, sel);
        Bump();
    }

    /// <summary>Select every row in the buffer — scrollback + live
    /// screen. Absolute-anchored, so scrolling after Select-All keeps
    /// the same region selected.</summary>
    public void SelectAll()
    {
        int sb   = _active.Scrollback.Count;
        int last = sb + Rows - 1;
        SetSelection(new TerminalSelection(0, 0, last, Cols - 1, SelectionMode.Line));
    }

    /// <summary>Programmatic selection in absolute-row coordinates.
    /// (0 = oldest scrollback line.) Out-of-range coords get clamped
    /// to the buffer's reachable range. Empty / inverted ranges are
    /// accepted (Normalized() handles ordering).</summary>
    public void Select(int startRow, int startCol, int endRow, int endCol)
    {
        int total = _active.Scrollback.Count + Rows - 1;
        int r1 = Math.Clamp(startRow, 0, total);
        int r2 = Math.Clamp(endRow,   0, total);
        int c1 = Math.Clamp(startCol, 0, Cols - 1);
        int c2 = Math.Clamp(endCol,   0, Cols - 1);
        SetSelection(new TerminalSelection(r1, c1, r2, c2, SelectionMode.Character));
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

    /// <summary>Walk a snapshot producing every match of
    /// <paramref name="needle"/> under <paramref name="options"/>
    /// (case-sensitivity, whole-word, regex). Safe to run off-thread
    /// against a <see cref="SnapshotRows"/> result. Checks
    /// <paramref name="ct"/> between rows so a superseded search
    /// returns quickly.</summary>
    public static List<SearchMatch> ScanMatches(
        TerminalCell[][] rows, string needle,
        SearchOptions options, System.Threading.CancellationToken ct)
        => SearchIndex.Scan(rows, needle, options, ct);

    /// <summary>Legacy two-arg form preserved for source compatibility
    /// with hosts and tests written against the case-insensitive
    /// default.</summary>
    public static List<SearchMatch> ScanMatches(
        TerminalCell[][] rows, string needle, System.Threading.CancellationToken ct)
        => SearchIndex.Scan(rows, needle, SearchOptions.Default, ct);

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

    /// <summary>Characters double-click word-selection treats as
    /// non-word boundaries, in addition to space and tab. Defaults
    /// to a typical shell set; the host can override (e.g. extend
    /// for SQL, narrow for path-like tokens).</summary>
    public string WordSeparators { get; set; } = " \t`~!@#$%^&*()-=+[{]}\\|;:'\",.<>/?";

    private bool IsWordChar(TerminalCell c)
    {
        if (c.Rune == 0) return false;
        // Plain ASCII fast path: avoid ConvertFromUtf32 for the 99%
        // case. Outside the BMP we fall through to a string compare.
        if (c.Rune <= 0xFFFF)
            return WordSeparators.IndexOf((char)c.Rune) < 0;
        return WordSeparators.IndexOf(char.ConvertFromUtf32(c.Rune), StringComparison.Ordinal) < 0;
    }

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

    /// <summary>Serialize the buffer to a VT-replayable string that
    /// reconstructs scrollback + live screen + cursor position when
    /// fed back through <see cref="Write(ReadOnlySpan{byte})"/>. SGR
    /// state is emitted as transitions so styled content survives
    /// round-trip. Useful for session save, snapshot tests, and
    /// "freeze this terminal" UX.
    ///
    /// <para>Currently captures: cell content (with combining-mark
    /// preservation), foreground/background (palette + RGB), bold /
    /// italic / underline / strikethrough / inverse / dim / blink, the
    /// SGR 4:N underline style, and the cursor position. Does NOT
    /// capture: alt-screen state, scroll region, DEC private modes,
    /// custom tab stops, OSC 8 hyperlinks. Restoration is best-effort
    /// — for bit-perfect round-trip use a transcript instead.</para>
    /// </summary>
    public string Serialize()
    {
        var sb = new StringBuilder();
        // Reset before we start so the receiver isn't holding leftover
        // state from before the replay.
        sb.Append("\x1b[0m");
        var prev = TerminalCell.Blank;
        // Scrollback rows first, then live screen.
        var sc = _active.Scrollback;
        for (int i = 0; i < sc.Count; i++) AppendRow(sb, sc[i], ref prev, isLast: false);
        for (int r = 0; r < Rows; r++)
            AppendRow(sb, _active.GetRow(r), ref prev, isLast: r == Rows - 1);
        // Restore default attributes and place the cursor.
        sb.Append("\x1b[0m");
        sb.Append($"\x1b[{CursorRow + 1};{CursorCol + 1}H");
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, TerminalCell[] row,
        ref TerminalCell prev, bool isLast)
    {
        for (int c = 0; c < row.Length; c++)
        {
            var cell = row[c];
            if ((cell.Flags2 & CellFlags2.IsContinuation) != 0) continue;
            EmitSgrTransition(sb, prev, cell);
            prev = cell;
            if (cell.Rune == 0)      sb.Append(' ');
            else if (cell.Rune <= 0xFFFF) sb.Append((char)cell.Rune);
            else                     sb.Append(char.ConvertFromUtf32(cell.Rune));
        }
        if (!isLast) sb.Append("\r\n");
    }

    private static void EmitSgrTransition(StringBuilder sb, TerminalCell from, TerminalCell to)
    {
        if (from.Flags == to.Flags && from.Flags2 == to.Flags2
            && from.FgIndex == to.FgIndex && from.BgIndex == to.BgIndex
            && from.FgRgb == to.FgRgb && from.BgRgb == to.BgRgb
            && from.UnderlineStyle == to.UnderlineStyle
            && from.UnderlineRgb == to.UnderlineRgb)
            return;
        // Reset and re-emit — simpler than diffing each attribute.
        sb.Append("\x1b[0");
        if ((to.Flags & CellFlags.Bold)          != 0) sb.Append(";1");
        if ((to.Flags & CellFlags.Dim)           != 0) sb.Append(";2");
        if ((to.Flags & CellFlags.Italic)        != 0) sb.Append(";3");
        if ((to.Flags & CellFlags.Underline)     != 0)
        {
            sb.Append(to.UnderlineStyle switch
            {
                UnderlineStyle.None or UnderlineStyle.Single => ";4",
                _                                            => $";4:{(int)to.UnderlineStyle}",
            });
        }
        if ((to.Flags2 & CellFlags2.Blink)       != 0) sb.Append(";5");
        if ((to.Flags & CellFlags.Inverse)       != 0) sb.Append(";7");
        if ((to.Flags & CellFlags.Strikethrough) != 0) sb.Append(";9");
        if ((to.Flags & CellFlags.FgRgb) != 0)
        {
            uint v = to.FgRgb;
            sb.Append($";38;2;{(v >> 16) & 0xFF};{(v >> 8) & 0xFF};{v & 0xFF}");
        }
        else if (to.FgIndex != 0)
        {
            sb.Append(to.FgIndex < 8  ? $";{30 + to.FgIndex}"
                    : to.FgIndex < 16 ? $";{82 + to.FgIndex}"
                                      : $";38;5;{to.FgIndex}");
        }
        if ((to.Flags & CellFlags.BgRgb) != 0)
        {
            uint v = to.BgRgb;
            sb.Append($";48;2;{(v >> 16) & 0xFF};{(v >> 8) & 0xFF};{v & 0xFF}");
        }
        else if (to.BgIndex != 0)
        {
            sb.Append(to.BgIndex < 8  ? $";{40 + to.BgIndex}"
                    : to.BgIndex < 16 ? $";{92 + to.BgIndex}"
                                      : $";48;5;{to.BgIndex}");
        }
        if ((to.Flags2 & CellFlags2.UlColorSet) != 0)
        {
            uint v = to.UnderlineRgb;
            sb.Append($";58;2;{(v >> 16) & 0xFF};{(v >> 8) & 0xFF};{v & 0xFF}");
        }
        sb.Append('m');
    }

    // ---- IParserActions ----

    public void Print(int rune)
    {
        if (rune == 0) return;

        if (_gSlots[_activeG] == Charset.DecSpecialGraphics)
            rune = DecGraphics.Translate(rune);

        int width = UnicodeWidth.Of(rune);

        // Width-0 runes (combining marks, ZWJ, VS15/VS16, bidi format)
        // attach to the preceding cell instead of consuming a column.
        // VS16 forces emoji presentation: the previous cell that was
        // narrow (text-presentation default) gets retro-widened into
        // a 2-column emoji cell. Other width-0 runes are dropped —
        // we don't carry combining marks through to render today.
        if (width == 0)
        {
            ApplyZeroWidth(rune);
            _lastPrintRune = rune;
            return;
        }

        // When the cursor has fallen off the right edge (past the
        // last valid column), behaviour depends on DECAWM: wrap if
        // on, stomp the right-margin cell if off. The wrap flag on
        // the new row tells reflow this row continues from above.
        // autoWrappedThisPrint: tells the col-0 stale-flag-clearing
        // logic below not to undo the wrap flag we just set here.
        bool autoWrappedThisPrint = false;
        if (CursorCol >= Cols)
        {
            if (AutoWrap)
            {
                CarriageReturn(); LineFeedInternal();
                _active.SetWrapped(CursorRow, true);
                autoWrappedThisPrint = true;
            }
            else { CursorCol = Cols - 1; }
        }

        // Wide glyphs need two columns; wrap if the right half would
        // spill off the line (or stomp if autowrap is disabled).
        if (width == 2 && CursorCol >= Cols - 1)
        {
            if (AutoWrap)
            {
                CarriageReturn(); LineFeedInternal();
                _active.SetWrapped(CursorRow, true);
                autoWrappedThisPrint = true;
            }
            else { CursorCol = Cols - 2; }
        }

        // Stale-wrap-flag eviction: if we're about to write the FIRST
        // column of a row that's marked as a continuation, and we
        // didn't get here through auto-wrap, the wrap flag is left
        // over from earlier content the row no longer holds (e.g. CR
        // + overwrite, cursor positioning + write). Drop it so reflow
        // doesn't rejoin two unrelated lines.
        if (CursorCol == 0 && !autoWrappedThisPrint && _active.GetWrapped(CursorRow))
            _active.SetWrapped(CursorRow, false);

        // Cache the active link id once per Print — this is the hot
        // path for any large text dump and the two reads (cell +
        // wide-char continuation) both resolve through two property
        // hops (_osc.ActiveLinkId). Cache removes any doubt about
        // whether JIT folded the common subexpression.
        ushort linkId = _osc.ActiveLinkId;

        var row  = _active.GetRow(CursorRow);
        var cell = _pen;
        cell.Rune        = rune;
        cell.HyperlinkId = linkId;

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

        // Preserve SGR-driven Flags2 bits (Blink, UlColorSet) from the
        // pen, but override the cell-shape flags (IsWide /
        // IsContinuation) we set based on the rune width.
        var penExtras = _pen.Flags2 & (CellFlags2.Blink | CellFlags2.UlColorSet);
        if ((penExtras & CellFlags2.Blink) != 0) HasBlinkContent = true;
        if (width == 2)
        {
            cell.Flags2 = CellFlags2.IsWide | penExtras;
            row[CursorCol] = cell;
            if (CursorCol + 1 < Cols)
            {
                var cont = _pen;
                cont.Rune        = 0;
                cont.Flags2      = CellFlags2.IsContinuation | penExtras;
                cont.HyperlinkId = linkId;
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

    /// <summary>Apply a zero-width codepoint (combining mark / ZWJ /
    /// VS) to the cell most recently written. VS16 retro-widens a
    /// narrow base into emoji presentation; other zero-width runes
    /// are dropped today (we don't store combining marks).</summary>
    private void ApplyZeroWidth(int rune)
    {
        if (!UnicodeWidth.IsVS16(rune)) return;
        // VS16: locate the most recently written cell. If the cursor
        // is at column 0, the base is on the previous row's right
        // edge (rare with autowrap on; happens after explicit CR).
        int row = CursorRow;
        int col = CursorCol - 1;
        if (col < 0)
        {
            if (row == 0) return;
            row = row - 1;
            col = Cols - 1;
        }
        var cells = _active.GetRow(row);
        // Skip continuation cells: VS16 after a wide character is
        // already-wide so there's nothing to retro-widen.
        if ((cells[col].Flags2 & CellFlags2.IsContinuation) != 0) return;
        if ((cells[col].Flags2 & CellFlags2.IsWide) != 0) return;
        // Promote to wide. Need a continuation slot at col+1; when the
        // base is at the right edge there's no slot, so just leave the
        // cell as narrow (text presentation wins by default in that
        // edge case).
        if (col + 1 >= Cols) return;
        cells[col].Flags2 |= CellFlags2.IsWide;
        var cont = cells[col];
        cont.Rune        = 0;
        cont.Flags2      = (cont.Flags2 & ~CellFlags2.IsWide) | CellFlags2.IsContinuation;
        cells[col + 1]   = cont;
        // Cursor advanced past the base when we wrote it; advance one
        // more column so the next print lands at the correct slot.
        if (CursorCol < Cols) CursorCol = Math.Min(Cols, CursorCol + 1);
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

    /// <summary>BEL (0x07) — the host's responsibility to honour
    /// (audible beep, visual flash, desktop notification, or just
    /// ignore). Fires synchronously while parsing, so a high-rate
    /// emitter ("ASCII art bombing") will fire many times in quick
    /// succession; consumers usually want to debounce.</summary>
    public event EventHandler? Bell;

    public void Execute(byte c0)
    {
        switch (c0)
        {
            case 0x07: Bell?.Invoke(this, EventArgs.Empty); return; // BEL
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

    public void CsiDispatchWithSub(char final, ReadOnlySpan<int> p, ReadOnlySpan<int> subs,
        string intermediates, char prefix)
    {
        // Sub-params currently only matter for SGR 4:N (underline
        // style). Stash the slice for ApplySgr to read; it's the
        // dominant CSI by frequency and we don't want to thread a span
        // through a half-dozen helpers that don't care about it.
        _csiSubs = subs;
        try { CsiDispatch(final, p, intermediates, prefix); }
        finally { _csiSubs = default; }
    }

    /// <summary>Sub-param slice for the in-flight CSI dispatch. Only
    /// non-empty inside <see cref="CsiDispatchWithSub"/>; outside, the
    /// SGR handler treats it as all-zeros.</summary>
    private ReadOnlySpan<int> _csiSubs
    {
        get => _csiSubsArr.AsSpan(0, _csiSubsLen);
        set
        {
            if (value.Length > _csiSubsArr.Length) Array.Resize(ref _csiSubsArr, value.Length);
            value.CopyTo(_csiSubsArr);
            _csiSubsLen = value.Length;
        }
    }
    private int[] _csiSubsArr = new int[32];
    private int _csiSubsLen;

    public void CsiDispatch(char final, ReadOnlySpan<int> p, string intermediates, char prefix)
    {
        // Custom handlers first — most-recent wins. Fall through if
        // they all return false. Handlers are responsible for emitting
        // their own replies via the buffer's reply path.
        if (_csiHandlers.TryGetValue((final, prefix), out var list))
        {
            for (int idx = list.Count - 1; idx >= 0; idx--)
            {
                if (list[idx](p, intermediates)) return;
            }
        }
        int p0 = p.Length > 0 ? p[0] : 0;
        int p1 = p.Length > 1 ? p[1] : 0;

        if (prefix == '?')
        {
            if (final == 'h') { SetDecMode(p, true);  return; }
            if (final == 'l') { SetDecMode(p, false); return; }
            // DECRQM (DEC private): CSI ? Pd $ p
            if (final == 'p' && intermediates == "$") { ReplyDecrqm(p0, isAnsi: false); return; }
            // DECSED — selective erase display.
            if (final == 'J') { EraseDisplay(p0); return; }
            // DECSEL — selective erase line.
            if (final == 'K') { EraseLine(p0); return; }
            // DECDSR — DEC-private DSR (CSI ? n).
            if (final == 'n') { HandleDecDsr(p0); return; }
            return;
        }

        if (prefix == '>')
        {
            // DA2 — secondary device attributes. xterm reports
            // model = 0 (xterm), version = 276, hw = 0.
            if (final == 'c') { ReplyAscii("\x1b[>0;276;0c"); return; }
            // XTMODKEYS: CSI > Pp ; Pv m — set keyboard-protocol option.
            // We only care about Pp == 4 (modifyOtherKeys) — Pv is the
            // level (0 / 1 / 2). 2 means "send CSI 27;mod;key~ for any
            // ambiguous Ctrl/Shift/Alt+ASCII combination". Other Pp
            // values (modifyCursorKeys, modifyFunctionKeys, ...) are
            // ignored — the level we surface is enough for Neovim,
            // helix, and the rest.
            if (final == 'm' && p.Length >= 1)
            {
                if (p[0] == 4)
                {
                    int level = p.Length >= 2 ? p[1] : 0;
                    ModifyOtherKeys = Math.Clamp(level, 0, 2);
                }
                return;
            }
            // CSI > Pp n — reset XTMODKEYS option.
            if (final == 'n' && p.Length >= 1 && p[0] == 4) { ModifyOtherKeys = 0; return; }
            return;
        }

        if (prefix == '=')
        {
            // DA3 — tertiary device attributes. Reply with empty
            // identification: DCS ! | <hexbytes> ST.
            if (final == 'c') { ReplyAscii("\x1bP!|00000000\x1b\\"); return; }
            return;
        }

        // DECSTR — soft reset. Private intermediate "!" followed by 'p'.
        if (intermediates == "!" && final == 'p') { SoftReset(); return; }

        // DECRQM (ANSI): CSI Pd $ p — non-private form.
        if (final == 'p' && intermediates == "$") { ReplyDecrqm(p0, isAnsi: true); return; }

        // DECSCA — CSI Ps " q. We accept it but treat protected/erasable
        // as the same (no DECSED protection enforcement yet).
        if (final == 'q' && intermediates == "\"") { return; }

        // DECIC / DECDC — insert/delete N columns at cursor.
        if (final == '}' && intermediates == "'") { InsertColumns(Max1(p0)); return; }
        if (final == '~' && intermediates == "'") { DeleteColumns(Max1(p0)); return; }

        // SL / SR — scroll left / right by N columns. Both take the
        // space intermediate (CSI Pn SP @ / SP A).
        if (final == '@' && intermediates == " ") { ScrollLeft (Max1(p0)); return; }
        if (final == 'A' && intermediates == " ") { ScrollRight(Max1(p0)); return; }

        // Any non-REP CSI invalidates the "last printable" state.
        if (final != 'b') _lastPrintRune = 0;

        switch (final)
        {
            case 'A': MoveCursorUp  (Max1(p0)); return;
            case 'B': MoveCursorDown(Max1(p0)); return;
            case 'C': CursorCol = Clamp(CursorCol + Max1(p0), 0, Cols - 1); return;
            case 'D': CursorCol = Clamp(CursorCol - Max1(p0), 0, Cols - 1); return;
            case 'E': CursorCol = 0; MoveCursorDown(Max1(p0)); return;
            case 'F': CursorCol = 0; MoveCursorUp  (Max1(p0)); return;
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
            case '`': CursorCol = Clamp((p0 > 0 ? p0 : 1) - 1, 0, Cols - 1); return; // HPA
            case 'a': CursorCol = Clamp(CursorCol + Max1(p0), 0, Cols - 1); return;  // HPR
            case 'd': CursorRow = Clamp((p0 > 0 ? p0 : 1) - 1, 0, Rows - 1); return;
            case 'e': MoveCursorDown(Max1(p0)); return;     // VPR
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
        TerminalLog.TraceProtocol(
            $"unhandled CSI: prefix='{(prefix == 0 ? ' ' : prefix)}' final='{final}' intermediates='{intermediates}'");
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
        if (_escHandlers.TryGetValue((final, intermediates), out var list))
        {
            for (int idx = list.Count - 1; idx >= 0; idx--)
            {
                if (list[idx](intermediates)) return;
            }
        }
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
        TerminalLog.TraceProtocol(
            $"unhandled ESC: final='{final}' intermediates='{intermediates}'");
    }

    public void OscDispatch(ReadOnlySpan<char> payload)
    {
        // Strip the leading "<id>;" so handlers receive just the body —
        // matches xterm.js's OscHandler signature. If no handler claims
        // the id, fall through to the built-in OscDispatcher.
        int semi = payload.IndexOf(';');
        if (semi > 0 && int.TryParse(payload[..semi], out int id)
            && _oscHandlers.TryGetValue(id, out var list))
        {
            var body = payload[(semi + 1)..];
            for (int idx = list.Count - 1; idx >= 0; idx--)
            {
                if (list[idx](body)) return;
            }
        }
        _osc.Dispatch(payload);
    }

    public void DcsDispatch(char final, ReadOnlySpan<int> p, string intermediates,
        char prefix, ReadOnlySpan<char> payload)
    {
        if (_dcsHandlers.TryGetValue((final, intermediates), out var list))
        {
            for (int idx = list.Count - 1; idx >= 0; idx--)
            {
                if (list[idx](p, intermediates, payload)) return;
            }
        }
        // DECRQSS: DCS $ q <selector> ST. Reply is DCS Ps $ r <value> ST,
        // where Ps = 1 (valid + value follows) or 0 (invalid). The
        // selector in the payload identifies which setting to report;
        // we cover the ones VT-compliant apps actually probe.
        if (final == 'q' && intermediates == "$")
        {
            HandleDecrqss(payload);
            return;
        }
        // Other DCS sequences (DECUDK, sixel, kitty graphics) — drop
        // until a host wires in a handler via RegisterDcsHandler.
        TerminalLog.TraceProtocol(
            $"unhandled DCS: prefix='{(prefix == 0 ? ' ' : prefix)}' final='{final}' intermediates='{intermediates}' payload-len={payload.Length}");
    }

    private void HandleDecrqss(ReadOnlySpan<char> selector)
    {
        // selector identifies the setting being queried. Match against
        // the short forms apps actually emit. Reply with `DCS 1 $ r ...
        // ST` on success, `DCS 0 $ r <selector> ST` on unsupported.
        string reply;
        if (selector.SequenceEqual("\"q"))  // DECSCA
            reply = "1$r0\"q";
        else if (selector.SequenceEqual("\"p"))  // DECSCL
            reply = "1$r61;1\"p";
        else if (selector.SequenceEqual("r"))    // DECSTBM
            reply = $"1$r{ScrollTop + 1};{ScrollBottom + 1}r";
        else if (selector.SequenceEqual(" q"))   // DECSCUSR
            reply = $"1$r{(int)CursorStyle switch
            {
                (int)CursorStyle.Block          => 2,
                (int)CursorStyle.UnderlineBlink => 3,
                (int)CursorStyle.Underline      => 4,
                (int)CursorStyle.BarBlink       => 5,
                (int)CursorStyle.Bar            => 6,
                _                                => 1,
            }} q";
        else if (selector.SequenceEqual("m"))    // SGR
            reply = $"1$r{FormatSgrSnapshot()}m";
        else
            reply = "0$r" + new string(selector);
        ReplyAscii("\x1bP" + reply + "\x1b\\");
    }

    private string FormatSgrSnapshot()
    {
        // Render the current pen as the SGR sequence that would
        // recreate it. Used by DECRQSS m and by Serialize. Keep the
        // ordering loose — matches what xterm emits.
        var sb = new StringBuilder("0");
        if ((_pen.Flags & CellFlags.Bold)          != 0) sb.Append(";1");
        if ((_pen.Flags & CellFlags.Dim)           != 0) sb.Append(";2");
        if ((_pen.Flags & CellFlags.Italic)        != 0) sb.Append(";3");
        if ((_pen.Flags & CellFlags.Underline) != 0)
        {
            sb.Append(_pen.UnderlineStyle switch
            {
                UnderlineStyle.Single or UnderlineStyle.None => ";4",
                _                                            => $";4:{(int)_pen.UnderlineStyle}",
            });
        }
        if ((_pen.Flags2 & CellFlags2.Blink)       != 0) sb.Append(";5");
        if ((_pen.Flags & CellFlags.Inverse)       != 0) sb.Append(";7");
        if ((_pen.Flags & CellFlags.Strikethrough) != 0) sb.Append(";9");
        if ((_pen.Flags & CellFlags.FgRgb) != 0)
        {
            uint v = _pen.FgRgb;
            sb.Append($";38:2::{(v >> 16) & 0xFF}:{(v >> 8) & 0xFF}:{v & 0xFF}");
        }
        else if (_pen.FgIndex != 0)
        {
            sb.Append(_pen.FgIndex < 8  ? $";{30 + _pen.FgIndex}"
                    : _pen.FgIndex < 16 ? $";{82 + _pen.FgIndex}"
                                        : $";38:5:{_pen.FgIndex}");
        }
        if ((_pen.Flags & CellFlags.BgRgb) != 0)
        {
            uint v = _pen.BgRgb;
            sb.Append($";48:2::{(v >> 16) & 0xFF}:{(v >> 8) & 0xFF}:{v & 0xFF}");
        }
        else if (_pen.BgIndex != 0)
        {
            sb.Append(_pen.BgIndex < 8  ? $";{40 + _pen.BgIndex}"
                    : _pen.BgIndex < 16 ? $";{92 + _pen.BgIndex}"
                                        : $";48:5:{_pen.BgIndex}");
        }
        return sb.ToString();
    }

    public void ReplyToPty(ReadOnlySpan<byte> bytes)
    {
        _pendingReplies.AddRange(bytes);
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
                default:
                    TerminalLog.TraceProtocol($"unhandled ANSI mode: {(on ? "SM" : "RM")} {m}");
                    break;
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

    /// <summary>Force-clear the active OSC 8 hyperlink id. Useful as
    /// a recovery path when the shell's <c>OSC 8 ; ; ST</c> close
    /// sequence got swallowed upstream and every subsequent printed
    /// cell is inheriting the stuck link id. Doesn't touch SGR pen,
    /// cursor, or screen contents.</summary>
    public void ClearActiveHyperlink()
    {
        _osc.ClearActiveHyperlink();
        Bump();
    }

    /// <summary>Public DECSTR (soft reset). Clears SGR pen, cursor
    /// visibility, scroll region, insert/origin modes, charset
    /// slots — but preserves screen contents and scrollback. Useful
    /// for hosts that want a "reset formatting" command without
    /// nuking history.</summary>
    public void SoftResetTerminal()
    {
        SoftReset();
        Bump();
    }

    /// <summary>Public RIS (full reset). Equivalent to receiving
    /// <c>ESC c</c>: clears both screens, scrollback, all DEC modes,
    /// SGR pen, cursor state, OSC 8 / title state. Hosts wire this
    /// to a "reset terminal" command.</summary>
    public void ResetTerminal()
    {
        FullReset();
        Bump();
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
        HasBlinkContent = false;
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
                case 9:    MouseMode = on ? 9    : 0; break;   // X10 mouse
                case 1000: MouseMode = on ? 1000 : 0; break;
                case 1002: MouseMode = on ? 1002 : 0; break;
                case 1003: MouseMode = on ? 1003 : 0; break;
                case 1004: FocusEvents = on; break;
                case 1006: MouseEncoding = on ? MouseEncoding.Sgr        : MouseEncoding.Default; break;
                case 1016: MouseEncoding = on ? MouseEncoding.SgrPixels  : MouseEncoding.Default; break;
                case 2004: BracketedPaste = on; break;
                case 45:   ReverseWraparound = on; break;          // DECSET 45
                case 1048:                                          // save/restore cursor without alt-screen
                    if (on) SaveCursor(); else RestoreCursor();
                    break;
                case 2026: SynchronizedOutput = on; break;          // synchronized output
                default:
                    TerminalLog.TraceProtocol($"unhandled DEC mode: {(on ? "DECSET" : "DECRST")} {m}");
                    break;
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

    private void HandleDecDsr(int p)
    {
        // DEC-private DSR replies are wrapped in CSI ? <body> R / n.
        switch (p)
        {
            case 6: // DECXCPR — extended cursor position with page param.
                ReplyAscii($"\x1b[?{CursorRow + 1};{CursorCol + 1};1R");
                return;
            case 15: ReplyAscii("\x1b[?13n"); return;   // printer status: not ready
            case 25: ReplyAscii("\x1b[?20n"); return;   // UDK status: locked
            case 26: ReplyAscii("\x1b[?27;1;0;0n"); return; // keyboard: North-American
            case 53: case 55: ReplyAscii("\x1b[?53n"); return; // locator status: no locator
        }
    }

    /// <summary>DECRQM reply. Ps = 0 not recognised, 1 set, 2 reset,
    /// 3 permanently set, 4 permanently reset. The ANSI form replies
    /// with no `?` prefix, the DEC form with `?`. We answer for the
    /// modes the buffer actually tracks; everything else gets 0.</summary>
    private void ReplyDecrqm(int mode, bool isAnsi)
    {
        int status = 0;
        if (isAnsi)
        {
            status = mode switch
            {
                4  => InsertMode      ? 1 : 2,  // IRM
                20 => LineFeedNewLine ? 1 : 2,  // LNM
                _  => 0,
            };
        }
        else
        {
            bool? value = mode switch
            {
                1    => ApplicationCursorKeys,
                5    => ReverseVideo,
                6    => OriginMode,
                7    => AutoWrap,
                9    => MouseMode == 9,
                25   => CursorVisible,
                45   => ReverseWraparound,
                47   => IsAltScreen,
                1000 => MouseMode == 1000,
                1002 => MouseMode == 1002,
                1003 => MouseMode == 1003,
                1004 => FocusEvents,
                1006 => MouseEncoding == MouseEncoding.Sgr,
                1016 => MouseEncoding == MouseEncoding.SgrPixels,
                1047 => IsAltScreen,
                1049 => IsAltScreen,
                2004 => BracketedPaste,
                2026 => SynchronizedOutput,
                _    => null,
            };
            status = value switch { null => 0, true => 1, false => 2 };
        }
        char prefix = isAnsi ? ' ' : '?';
        if (isAnsi) ReplyAscii($"\x1b[{mode};{status}$y");
        else        ReplyAscii($"\x1b[?{mode};{status}$y");
    }

    /// <summary>SL — shift every row in the scroll region left by
    /// <paramref name="n"/> columns. Vacated cells on the right become
    /// blanks (with the current background).</summary>
    private void ScrollLeft(int n)
    {
        for (int r = ScrollTop; r <= ScrollBottom; r++)
        {
            var row = _active.GetRow(r);
            int src = n, dst = 0;
            while (src < Cols) row[dst++] = row[src++];
            while (dst < Cols) row[dst++] = BlankPenCell();
            ScrubRowWidePairs(row);
        }
    }

    /// <summary>SR — shift every row in the scroll region right by
    /// <paramref name="n"/> columns. Vacated cells on the left become
    /// blanks (with the current background).</summary>
    private void ScrollRight(int n)
    {
        for (int r = ScrollTop; r <= ScrollBottom; r++)
        {
            var row = _active.GetRow(r);
            for (int c = Cols - 1; c >= n; c--) row[c] = row[c - n];
            for (int c = 0; c < Math.Min(n, Cols); c++) row[c] = BlankPenCell();
            ScrubRowWidePairs(row);
        }
    }

    /// <summary>DECIC — insert <paramref name="n"/> blank columns at the
    /// cursor's column position across every row. Cells past the right
    /// margin are dropped. No-op outside the scroll region.</summary>
    private void InsertColumns(int n)
    {
        if (CursorRow < ScrollTop || CursorRow > ScrollBottom) return;
        for (int r = ScrollTop; r <= ScrollBottom; r++)
        {
            var row = _active.GetRow(r);
            for (int c = Cols - 1; c >= CursorCol + n; c--) row[c] = row[c - n];
            for (int c = CursorCol; c < Math.Min(CursorCol + n, Cols); c++) row[c] = BlankPenCell();
            ScrubRowWidePairs(row);
        }
    }

    /// <summary>DECDC — delete <paramref name="n"/> columns at the
    /// cursor's column position across every row. Blanks fill from the
    /// right. No-op outside the scroll region.</summary>
    private void DeleteColumns(int n)
    {
        if (CursorRow < ScrollTop || CursorRow > ScrollBottom) return;
        for (int r = ScrollTop; r <= ScrollBottom; r++)
        {
            var row = _active.GetRow(r);
            int src = CursorCol + n, dst = CursorCol;
            while (src < Cols) row[dst++] = row[src++];
            while (dst < Cols) row[dst++] = BlankPenCell();
            ScrubRowWidePairs(row);
        }
    }

    private void EnterAlt(bool saveCursor)
    {
        // DECSET 1049 only has effect when we're actually switching
        // screens — if we're already on alt, don't overwrite the
        // saved-cursor slot with the alt screen's cursor.
        if (_active == _alternate) return;
        // 1049 semantics: "save cursor as in DECSC" — i.e. into the
        // primary screen's DECSC slot, the one we'll restore from on
        // 1049-reset. Saving to the *alt* slot (as an earlier version
        // did) was racy because DECSC inside the alt screen also
        // writes there, overwriting the 1049 anchor before the app
        // ever leaves alt mode.
        if (saveCursor) _primarySaved = SnapshotCursor();
        _active = _alternate;
        _active.Clear();
        ScrollTop = 0; ScrollBottom = Rows - 1;
    }

    private void LeaveAlt(bool restoreCursor)
    {
        if (_active != _alternate) return;
        _active = _primary;
        ScrollTop = 0; ScrollBottom = Rows - 1;
        if (restoreCursor) ApplyCursor(_primarySaved);
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

    /// <summary>
    /// CUU (cursor-up). Per xterm, when the cursor starts inside the
    /// scroll region [ScrollTop, ScrollBottom] the upward motion
    /// clamps at ScrollTop — it does not pass above the top of the
    /// region. When the cursor starts above the region (legal in
    /// origin-mode-off), it clamps to the screen top instead. CPL
    /// (CSI F) shares this behaviour.
    /// </summary>
    private void MoveCursorUp(int n)
    {
        int min = CursorRow >= ScrollTop ? ScrollTop : 0;
        CursorRow = Clamp(CursorRow - n, min, Rows - 1);
    }

    /// <summary>
    /// CUD (cursor-down) / VPR / CNL. Symmetric to <see cref="MoveCursorUp"/>:
    /// when the cursor starts inside the scroll region, downward motion
    /// clamps at ScrollBottom. When it starts below the region, it
    /// clamps to the screen bottom.
    /// </summary>
    private void MoveCursorDown(int n)
    {
        int max = CursorRow <= ScrollBottom ? ScrollBottom : Rows - 1;
        CursorRow = Clamp(CursorRow + n, 0, max);
    }

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
        // Erasing leaves the row's wrap flag stale: the cells that
        // were the continuation are gone, so reflow shouldn't rejoin
        // this row with the line above. Modes 1 and 2 wipe the leading
        // cells, which is what reflow keys off; mode 0 (cursor → end)
        // doesn't touch the leading cells, so leave the flag alone
        // there.
        if (mode == 1 || mode == 2)
            _active.SetWrapped(CursorRow, false);
        ScrubRowWidePairs(row);
    }

    private void ClearRow(int r)
    {
        var row = _active.GetRow(r);
        for (int c = 0; c < Cols; c++) row[c] = BlankPenCell();
        _active.SetWrapped(r, false);
        // Whole-row blank can't have orphans (no IsWide / IsContinuation
        // anywhere) so the scrub would no-op. Skip the walk.
    }

    private void EraseChars(int n)
    {
        var row = _active.GetRow(CursorRow);
        for (int i = 0; i < n && CursorCol + i < Cols; i++) row[CursorCol + i] = BlankPenCell();
        ScrubRowWidePairs(row);
    }

    private void DeleteChars(int n)
    {
        var row = _active.GetRow(CursorRow);
        int src = CursorCol + n, dst = CursorCol;
        while (src < Cols) row[dst++] = row[src++];
        while (dst < Cols) row[dst++] = BlankPenCell();
        ScrubRowWidePairs(row);
    }

    private void InsertBlanks(int n)
    {
        var row = _active.GetRow(CursorRow);
        for (int c = Cols - 1;            c >= CursorCol + n; c--) row[c] = row[c - n];
        for (int c = CursorCol; c < CursorCol + n && c < Cols; c++) row[c] = BlankPenCell();
        ScrubRowWidePairs(row);
    }

    /// <summary>Normalise wide-cell pair integrity across a row.
    /// Any cell-erase / cell-shift operation that lands on one half
    /// of a wide pair without honouring the other half will leave
    /// "orphans": an IsWide cell whose neighbour is no longer
    /// IsContinuation, or an IsContinuation cell whose left
    /// neighbour is no longer IsWide. The renderer skips
    /// IsContinuation cells, so orphans render as invisible gaps —
    /// the "double-space and missing characters" symptom seen in
    /// streaming sessions that mix CSI X / CSI P / DECIC / DECDC /
    /// SL / SR with CJK or emoji content.
    ///
    /// <para>O(cols), called once per CSI mutation. CSI dispatches
    /// are far less frequent than per-character Print, so this is
    /// not on a hot path.</para>
    /// </summary>
    private static void ScrubRowWidePairs(TerminalCell[] row)
    {
        for (int c = 0; c < row.Length; c++)
        {
            bool isCont = (row[c].Flags2 & CellFlags2.IsContinuation) != 0;
            bool isWide = (row[c].Flags2 & CellFlags2.IsWide)         != 0;
            if (isCont && (c == 0 || (row[c - 1].Flags2 & CellFlags2.IsWide) == 0))
            {
                // Continuation with no preceding wide-left → strip
                // the flag and clear the (already-blank) rune so the
                // renderer treats it as an ordinary cell.
                row[c].Flags2 &= ~CellFlags2.IsContinuation;
                row[c].Rune    = 0;
            }
            if (isWide && (c + 1 >= row.Length
                || (row[c + 1].Flags2 & CellFlags2.IsContinuation) == 0))
            {
                // Wide-left with no following continuation → demote
                // to narrow. Keep the rune; the renderer can still
                // draw it (clipped to one column if it overflows,
                // which matches what a narrow-rendered wide glyph
                // looks like in any other terminal).
                row[c].Flags2 &= ~CellFlags2.IsWide;
            }
        }
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
        _primary.ClearScrollback();
        _alternate.Clear();
        _alternate.ClearScrollback();
        CursorRow = CursorCol = 0;
        _pen = TerminalCell.Blank;
        CursorVisible = true;
        CursorStyle = CursorStyle.BlockBlink;
        ScrollTop = 0; ScrollBottom = Rows - 1;
        _activeG = 0; _gSlots[0] = Charset.Ascii; _gSlots[1] = Charset.Ascii;
        MouseMode = 0; MouseEncoding = MouseEncoding.Default;
        BracketedPaste = false; FocusEvents = false;
        ApplicationCursorKeys = false; ApplicationKeypad = false;
        AutoWrap = true; OriginMode = false; ReverseVideo = false;
        InsertMode = false; LineFeedNewLine = false;
        ReverseWraparound = false; SynchronizedOutput = false;
        ModifyOtherKeys = 0;
        _viewport.Reset();
        if (Selection != null) SetSelection(null);
        _osc.Reset();
        _tabStops = null; // will rebuild with defaults on next access
        _lastPrintRune = 0;
        HasBlinkContent = false;
        _parser.Reset();
    }

    // ---- SGR ----

    private void ApplySgr(ReadOnlySpan<int> p)
    {
        if (p.Length == 0) { _pen = TerminalCell.Blank; return; }

        var subs = _csiSubs;
        int i = 0;
        while (i < p.Length)
        {
            switch (p[i])
            {
                case 0:   _pen = TerminalCell.Blank; break;
                case 1:   _pen.Flags  |=  CellFlags.Bold;          break;
                case 2:   _pen.Flags  |=  CellFlags.Dim;           break;
                case 3:   _pen.Flags  |=  CellFlags.Italic;        break;
                case 4:
                {
                    _pen.Flags |= CellFlags.Underline;
                    // SGR 4:N — colon sub-parameter selects style.
                    int sub = i < subs.Length ? subs[i] : 0;
                    _pen.UnderlineStyle = sub switch
                    {
                        0 => UnderlineStyle.Single,  // bare 4 = single
                        1 => UnderlineStyle.Single,
                        2 => UnderlineStyle.Double,
                        3 => UnderlineStyle.Curly,
                        4 => UnderlineStyle.Dotted,
                        5 => UnderlineStyle.Dashed,
                        _ => UnderlineStyle.Single,
                    };
                    break;
                }
                case 5:
                case 6:   _pen.Flags2 |=  CellFlags2.Blink;        break;
                case 7:   _pen.Flags  |=  CellFlags.Inverse;       break;
                case 9:   _pen.Flags  |=  CellFlags.Strikethrough; break;
                case 21:  _pen.Flags  |=  CellFlags.Underline;
                          _pen.UnderlineStyle = UnderlineStyle.Double; break;
                case 22:  _pen.Flags  &= ~(CellFlags.Bold | CellFlags.Dim); break;
                case 23:  _pen.Flags  &= ~CellFlags.Italic;        break;
                case 24:  _pen.Flags  &= ~CellFlags.Underline;
                          _pen.UnderlineStyle = UnderlineStyle.None;  break;
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

                case 38: i += ApplyExtColor(p, i, ColorTarget.Foreground); break;
                case 48: i += ApplyExtColor(p, i, ColorTarget.Background); break;
                case 58: i += ApplyExtColor(p, i, ColorTarget.Underline);  break;
                case 59: // reset underline colour
                    _pen.Flags2 &= ~CellFlags2.UlColorSet;
                    _pen.UnderlineRgb = 0;
                    break;
            }
            i++;
        }
    }

    private enum ColorTarget { Foreground, Background, Underline }

    /// <summary>Returns the number of params beyond <paramref name="i"/>
    /// the caller should skip (0 / 2 / 4 depending on 256 vs RGB).
    /// Handles the colon form (SGR 38:2::R:G:B with an empty colour-
    /// space slot) and the semicolon form (SGR 38;2;R;G;B) — both
    /// surface as plain primary params here, so the caller doesn't
    /// need to differentiate.</summary>
    private int ApplyExtColor(ReadOnlySpan<int> p, int i, ColorTarget target)
    {
        if (i + 1 >= p.Length) return 0;
        int kind = p[i + 1];
        if (kind == 5 && i + 2 < p.Length)
        {
            byte idx = (byte)(p[i + 2] & 0xFF);
            switch (target)
            {
                case ColorTarget.Foreground: SetFgIdx(idx); break;
                case ColorTarget.Background: SetBgIdx(idx); break;
                case ColorTarget.Underline:
                    _pen.UnderlineRgb = (uint)((TerminalPalette.Indexed[idx].R << 16)
                                       | (TerminalPalette.Indexed[idx].G << 8)
                                       | TerminalPalette.Indexed[idx].B);
                    _pen.Flags2 |= CellFlags2.UlColorSet;
                    break;
            }
            return 2;
        }
        if (kind == 2)
        {
            // Colon form may include an empty colour-space slot
            // (SGR 38:2::R:G:B), surfaced as a 0 between kind=2 and R.
            // Detect by looking for 5 trailing params instead of 3 —
            // the empty slot is a primary in that case. Fall back to
            // the 3-param semicolon form when there are only three.
            if (i + 5 < p.Length)
            {
                // Either p[i+2] is the empty colour-space slot (most
                // emitters) or it's R. Heuristic: an emitter that
                // writes 5 components after `2` always intends the
                // colon form, with an empty colour-space slot.
                int r = p[i + 3], g = p[i + 4], b = p[i + 5];
                ApplyExtRgb(target, r, g, b);
                return 5;
            }
            if (i + 4 < p.Length)
            {
                int r = p[i + 2], g = p[i + 3], b = p[i + 4];
                ApplyExtRgb(target, r, g, b);
                return 4;
            }
        }
        return 0;
    }

    private void ApplyExtRgb(ColorTarget target, int r, int g, int b)
    {
        uint packed = (uint)((r & 0xFF) << 16 | (g & 0xFF) << 8 | (b & 0xFF));
        switch (target)
        {
            case ColorTarget.Foreground:
                _pen.FgRgb = packed;
                _pen.Flags |= CellFlags.FgRgb;
                break;
            case ColorTarget.Background:
                _pen.BgRgb = packed;
                _pen.Flags |= CellFlags.BgRgb;
                break;
            case ColorTarget.Underline:
                _pen.UnderlineRgb = packed;
                _pen.Flags2 |= CellFlags2.UlColorSet;
                break;
        }
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
