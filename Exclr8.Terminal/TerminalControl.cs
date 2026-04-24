using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Exclr8.Terminal.Buffer;
using Exclr8.Terminal.Input;
using Exclr8.Terminal.ProcessWatch;
using Exclr8.Terminal.Render;

namespace Exclr8.Terminal;

/// <summary>
/// Native Avalonia terminal renderer. Consumers feed raw PTY bytes via
/// <see cref="Write(byte[])"/>, handle <see cref="Input"/> (bytes the
/// user typed), <see cref="Output"/> (DSR/DA replies the terminal
/// requested we forward to the PTY), <see cref="Resized"/> (new cell
/// grid), and optionally <see cref="HyperlinkClicked"/>.
/// </summary>
public class TerminalControl : Control, IDisposable
{
    private readonly TerminalRenderer _renderer;
    private readonly TerminalBuffer   _buffer;
    private int _lastRevision = -1;

    // Mouse-click tracking for selection (double/triple click word/line).
    private bool _mouseDown;
    private int  _pressedBtn   = -1;
    private int  _lastClickRow = -1, _lastClickCol = -1;
    private int  _clickCount;
    private DateTime _lastClickTime = DateTime.MinValue;
    private static readonly TimeSpan DoubleClickThreshold = TimeSpan.FromMilliseconds(400);

    // Deferred-selection state: we hold off creating a Selection until
    // the pointer actually moves to a different cell. A plain click with
    // no drag should never produce a 1-cell "smudge" selection — click
    // alone clears any existing selection, drag starts a new one.
    private bool _selectionPending;
    private int  _pressedRow = -1, _pressedCol = -1;

    // Scrollbar drag state. When the user pointer-presses on the right-
    // edge strip we enter scrollbar-drag mode; subsequent PointerMoved
    // events update ScrollOffset until PointerReleased.
    private bool _scrollbarDrag;

    // Cursor blink timer — toggles the renderer's BlinkVisible flag.
    private readonly DispatcherTimer _blinkTimer;
    private bool _blinkVisible = true;

    // Auto-hide scrollbar: visible during scrolling and while the
    // pointer is inside the right-edge hit zone; fades out after a
    // short idle. The timer ticks at ~60Hz while the bar is on screen;
    // we stop it once opacity reaches zero to avoid idle CPU wakeups.
    private readonly DispatcherTimer _scrollbarTimer;
    private DateTime _scrollbarShownAt = DateTime.MinValue;
    private static readonly TimeSpan ScrollbarIdleDelay    = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan ScrollbarFadeDuration = TimeSpan.FromMilliseconds(250);

    private bool _altHeld;
    private TerminalTheme? _colorScheme;

    /// <summary>User typed — payload is the byte sequence ready for the
    /// PTY writer.</summary>
    public event EventHandler<ReadOnlyMemory<byte>>? Input;

    /// <summary>Terminal wants bytes sent back to the PTY (DSR / DA
    /// replies). The consumer forwards to <c>pty.WriterStream</c>.</summary>
    public event EventHandler<ReadOnlyMemory<byte>>? Output;

    /// <summary>Cell grid dimensions changed.</summary>
    public event EventHandler<(int Cols, int Rows)>? Resized;

    /// <summary>User clicked an OSC 8 hyperlink.</summary>
    public event EventHandler<string>? HyperlinkClicked;

    public TerminalBuffer Buffer => _buffer;

    /// <summary>Local observer of user input flowing through the
    /// terminal. Subscribe to <see cref="InputEventStream.LineCommitted"/>
    /// to react to committed lines (running-process badge, Claude
    /// slash-commands, cwd tracking, dangerous-command warnings,
    /// etc.) without each feature re-parsing the byte stream.</summary>
    public InputEventStream InputEvents { get; } = new();

    /// <summary>Emit user input to both subscribers (host PTY writer)
    /// and the local observer stream. <paramref name="origin"/> lets
    /// observers distinguish Typed / Pasted / Programmatic sources.</summary>
    private void RaiseInput(byte[] payload, InputLineOrigin origin)
    {
        Input?.Invoke(this, payload);
        InputEvents.Feed(payload, origin);
    }

    // ------------------------------------------------------------------
    // Process-tree watching.
    //
    // Exposes OS-level "something new spawned under the shell" events
    // (Windows WMI, macOS kqueue, no-op elsewhere) so consumers don't
    // have to poll the process table or know which platform they're
    // on. Lazily activated: the underlying watcher is only constructed
    // when the first subscriber attaches AND a root pid is set, and
    // disposed when the last subscriber detaches. Sets with no
    // subscribers pay nothing.
    //
    // Consumers typically set RootProcessId to the shell pid at spawn,
    // subscribe to ProcessTreeChanged, and interpret the stream
    // themselves (running-process badge, session state, etc.). The
    // control chain-watches each new child automatically so grandchild
    // forks (make → gcc, shell-in-shell) surface without any effort
    // from the subscriber.
    // ------------------------------------------------------------------

    private readonly object _processWatchLock = new();
    private readonly HashSet<int> _watchedPids = new();
    private IProcessChildWatcher? _processWatcher;
    private int _processTreeSubscribers;
    private int _rootProcessId;
    private Action<ProcessTreeChange>? _processTreeChangedInner;

    /// <summary>The shell / session root pid the terminal's watcher
    /// should hang off. VibeCoder sets this to the PTY's pid on spawn
    /// and back to 0 on teardown. Changing it while subscribers are
    /// attached re-targets the watcher live.</summary>
    public int RootProcessId
    {
        get => _rootProcessId;
        set
        {
            lock (_processWatchLock)
            {
                if (_rootProcessId == value) return;
                // Drop everything from the previous root — grandchildren
                // were chain-watched through it and are no longer
                // meaningful.
                if (_processWatcher != null) UnwatchAll_Locked();
                _rootProcessId = value;
                if (_processWatcher != null && _rootProcessId > 0)
                    Watch_Locked(_rootProcessId);
            }
        }
    }

    /// <summary>Something new has appeared (<see cref="ProcessTreeChangeKind.Created"/>)
    /// or disappeared (<see cref="ProcessTreeChangeKind.Exited"/>) in
    /// the process subtree rooted at <see cref="RootProcessId"/>.
    /// Attaching the first handler starts the OS-level watcher;
    /// detaching the last handler stops it.</summary>
    public event Action<ProcessTreeChange>? ProcessTreeChanged
    {
        add
        {
            if (value == null) return;
            lock (_processWatchLock)
            {
                _processTreeChangedInner += value;
                _processTreeSubscribers++;
                EnsureWatcherStarted_Locked();
            }
        }
        remove
        {
            if (value == null) return;
            lock (_processWatchLock)
            {
                _processTreeChangedInner -= value;
                _processTreeSubscribers--;
                if (_processTreeSubscribers <= 0) StopWatcher_Locked();
            }
        }
    }

    private void EnsureWatcherStarted_Locked()
    {
        if (_processWatcher != null) return;
        if (_processTreeSubscribers <= 0) return;
        try
        {
            var w = ProcessChildWatcherFactory.Create();
            w.ChildCreated  += OnWatcherChildCreated;
            w.ProcessExited += OnWatcherProcessExited;
            _processWatcher = w;
            if (_rootProcessId > 0) Watch_Locked(_rootProcessId);
        }
        catch (Exception ex)
        {
            TerminalLog.Error($"[TerminalControl] process-watcher start failed: {ex.Message}");
        }
    }

    private void StopWatcher_Locked()
    {
        var w = _processWatcher;
        _processWatcher = null;
        _watchedPids.Clear();
        // Note: _processTreeSubscribers is not reset here — the
        // subscriber count is maintained by the event add/remove
        // accessors, not by us.
        if (w == null) return;
        w.ChildCreated  -= OnWatcherChildCreated;
        w.ProcessExited -= OnWatcherProcessExited;
        try { w.Dispose(); } catch { }
    }

    private void Watch_Locked(int pid)
    {
        if (pid <= 0) return;
        if (!_watchedPids.Add(pid)) return;
        try { _processWatcher?.Watch(pid); } catch (Exception ex)
        { TerminalLog.Error($"[TerminalControl] Watch({pid}) failed: {ex.Message}"); }
    }

    private void UnwatchAll_Locked()
    {
        var w = _processWatcher;
        if (w != null)
        {
            foreach (var p in _watchedPids)
            { try { w.Unwatch(p); } catch { } }
        }
        _watchedPids.Clear();
    }

    private void OnWatcherChildCreated(ProcessChildEvent e)
    {
        // Chain-watch so the next generation of forks surfaces too.
        // Do this synchronously on the pump thread (backed by the
        // lock) so a grandchild spawned back-to-back with its parent
        // isn't missed.
        lock (_processWatchLock) Watch_Locked(e.ChildPid);

        if (_processTreeChangedInner == null) return;
        var change = new ProcessTreeChange(
            Kind:        ProcessTreeChangeKind.Created,
            Pid:         e.ChildPid,
            ParentPid:   e.ParentPid,
            Name:        e.Name,
            CommandLine: e.CommandLine);
        DispatchToUi(change, "created");
    }

    private void OnWatcherProcessExited(int pid)
    {
        lock (_processWatchLock) _watchedPids.Remove(pid);
        if (_processTreeChangedInner == null) return;
        var change = new ProcessTreeChange(
            Kind:        ProcessTreeChangeKind.Exited,
            Pid:         pid,
            ParentPid:   0,
            Name:        null,
            CommandLine: null);
        DispatchToUi(change, "exited");
    }

    /// <summary>Subscribers to <see cref="ProcessTreeChanged"/> almost
    /// certainly touch UI state (badges, labels, menu items). The
    /// underlying watchers fire on non-UI threads (kqueue pump on
    /// macOS, WMI callback pool on Windows), so we marshal onto the
    /// Avalonia dispatcher before raising the event.</summary>
    private void DispatchToUi(ProcessTreeChange change, string kind)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try { _processTreeChangedInner?.Invoke(change); }
            catch (Exception ex)
            {
                TerminalLog.Error(
                    $"[TerminalControl] ProcessTreeChanged {kind} dispatch: {ex.Message}");
            }
        });
    }

    /// <summary>Optional color overrides. Null = defaults. Deliberately
    /// not named <c>Theme</c> so it doesn't collide with Avalonia's
    /// <see cref="StyledElement.Theme"/> (which expects a
    /// <c>ControlTheme</c>, not a colour palette).</summary>
    public TerminalTheme? ColorScheme
    {
        get => _colorScheme;
        set { _colorScheme = value; InvalidateVisual(); }
    }

    public TerminalControl()
    {
        Focusable    = true;
        ClipToBounds = true;

        // I-beam over the cell grid so the hotspot sits at the centre
        // of the cursor and the pointer visually aligns with the
        // character it's over. The default arrow's hotspot is at the
        // top-left tip which makes drag-selection feel offset.
        Cursor = new Cursor(StandardCursorType.Ibeam);

        _renderer = new TerminalRenderer();
        _buffer   = new TerminalBuffer(80, 24);
        _buffer.Changed += (_, _) => InvalidateVisual();

        this.GetObservable(BoundsProperty)
            .Subscribe(new ActionObserver<Rect>(_ => RecomputeGrid()));

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _blinkTimer.Tick += (_, _) =>
        {
            _blinkVisible          = !_blinkVisible;
            _renderer.BlinkVisible = _blinkVisible;
            // Unconditional repaint every tick. We don't walk the grid
            // to check whether any cell carries SGR 5 (Blink); cheap
            // enough at 2 Hz, and covers both blinking-cursor and
            // blinking-content cases without extra state tracking.
            InvalidateVisual();
        };
        _blinkTimer.Start();

        _scrollbarTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _scrollbarTimer.Tick += OnScrollbarTick;
    }

    /// <summary>Surface "scrollbar-worthy activity". Snaps opacity to
    /// 1.0, starts the tick timer, and requests a repaint. Anything
    /// that involves the scrollback viewport calls this.</summary>
    private void ShowScrollbar()
    {
        if (_buffer.ScrollbackCount <= 0) return;
        _scrollbarShownAt = DateTime.UtcNow;
        _renderer.ScrollbarOpacity = 1.0;
        if (!_scrollbarTimer.IsEnabled) _scrollbarTimer.Start();
        InvalidateVisual();
    }

    private void OnScrollbarTick(object? s, EventArgs e)
    {
        // Dragging the thumb pins the bar at full opacity.
        if (_scrollbarDrag)
        {
            _scrollbarShownAt = DateTime.UtcNow;
            _renderer.ScrollbarOpacity = 1.0;
            return;
        }

        var elapsed = DateTime.UtcNow - _scrollbarShownAt;
        if (elapsed < ScrollbarIdleDelay)
        {
            _renderer.ScrollbarOpacity = 1.0;
            return;
        }

        var fade = (elapsed - ScrollbarIdleDelay).TotalMilliseconds / ScrollbarFadeDuration.TotalMilliseconds;
        double newOpacity = Math.Max(0, 1.0 - fade);
        _renderer.ScrollbarOpacity = newOpacity;
        InvalidateVisual();
        if (newOpacity <= 0.001) _scrollbarTimer.Stop();
    }

    // ---- PTY I/O ----

    public void Write(ReadOnlySpan<byte> bytes)
    {
        _buffer.Write(bytes);
        var replies = _buffer.TakeReplies();
        if (replies != null) Output?.Invoke(this, replies);
        if (_buffer.Revision != _lastRevision) InvalidateVisual();
    }

    public void Write(byte[] bytes) => Write(bytes.AsSpan());

    /// <summary>Hard cap on paste payload size. Past this the paste
    /// is silently dropped — shells don't handle a 100 MiB paste
    /// gracefully and we don't want to surprise the host process.</summary>
    public const int PasteMaxBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Paste text into the terminal. Wraps in <c>ESC[200~</c> /
    /// <c>ESC[201~</c> when DECSET 2004 (bracketed paste) is active —
    /// lets the shell distinguish typed vs pasted input. Rejects
    /// anything containing NUL (0x00), which is a tell-tale sign of a
    /// mis-identified binary payload that would confuse a PTY.
    /// </summary>
    public void Paste(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (text.IndexOf('\0') >= 0) return; // binary payload — refuse
        var inner = Encoding.UTF8.GetBytes(text);
        if (inner.Length > PasteMaxBytes) return;

        // Paste is about to push PTY bytes that will move the cursor
        // and almost certainly paint over wherever the selection was.
        // Drop the selection now so the stale highlight doesn't linger.
        _buffer.ClearSelection();

        byte[] payload;
        if (_buffer.BracketedPaste)
        {
            // ESC[200~ text ESC[201~. Note: do NOT translate CR
            // inside the brackets — bracketed paste intentionally
            // lets the shell see the raw newlines so it can decide
            // how to handle them (often, interpret them as input
            // separators).
            payload = new byte[inner.Length + 12];
            "\x1b[200~"u8.CopyTo(payload);
            inner.CopyTo(payload, 6);
            "\x1b[201~"u8.CopyTo(payload.AsSpan(6 + inner.Length));
        }
        else
        {
            payload = inner;
        }
        RaiseInput(payload, InputLineOrigin.Pasted);
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        _renderer.Render(ctx, _buffer, Bounds.Size, IsFocused, _colorScheme);
        _lastRevision = _buffer.Revision;
    }

    // Minimum grid size we'll honour. Anything smaller is almost
    // certainly a transient layout pass (e.g. mid-reparent after a
    // MoveCell) — resizing to those dimensions would shove live-screen
    // rows into scrollback and trigger a SIGWINCH redraw from the
    // shell, which looks to the user like the history got duplicated.
    private const int MinUsableCols = 10;
    private const int MinUsableRows = 3;

    private void RecomputeGrid()
    {
        var (cols, rows) = _renderer.ComputeGrid(Bounds.Size);
        if (cols < MinUsableCols || rows < MinUsableRows) return;
        if (cols == _buffer.Cols && rows == _buffer.Rows) return;
        _buffer.Resize(cols, rows);
        Resized?.Invoke(this, (cols, rows));
        InvalidateVisual();
    }

    // ---- Font zoom ----

    /// <summary>Absolute terminal font size (pt). Mostly for hosts that
    /// want to push a user-configured size from Settings. Keyboard
    /// zoom uses <see cref="AdjustFontSize"/> / <see cref="ResetFontSize"/>.</summary>
    public double FontSize
    {
        get => _renderer.FontSize;
        set
        {
            if (Math.Abs(_renderer.FontSize - value) < 0.01) return;
            _renderer.FontSize = value;
            RecomputeGrid();
            InvalidateVisual();
        }
    }

    /// <summary>Terminal font family. Passed straight to Avalonia's
    /// <see cref="Typeface"/> constructor, so both simple family names
    /// (<c>"Menlo"</c>) and fallback-list syntax
    /// (<c>"fonts:JetBrainsMono#JetBrains Mono, Menlo, monospace"</c>)
    /// work. Triggers cell re-measure and grid reflow.</summary>
    public string FontFamily
    {
        get => _renderer.FontFamily;
        set
        {
            if (_renderer.FontFamily == value) return;
            _renderer.FontFamily = value;
            RecomputeGrid();
            InvalidateVisual();
        }
    }

    /// <summary>Step the terminal font size by whole points. Positive
    /// direction enlarges (Cmd+=), negative shrinks (Cmd+-). Reflows
    /// the grid so the cell count matches the new cell metrics.</summary>
    public void AdjustFontSize(int direction)
    {
        _renderer.FontSize += direction;
        RecomputeGrid();
        InvalidateVisual();
    }

    /// <summary>Reset to the font size captured at construction
    /// (Cmd+0).</summary>
    public void ResetFontSize()
    {
        _renderer.FontSize = _renderer.DefaultFontSize;
        RecomputeGrid();
        InvalidateVisual();
    }

    // ---- Keyboard ----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        _altHeld = (e.KeyModifiers & KeyModifiers.Alt) != 0;

        bool isMac = OperatingSystem.IsMacOS();
        bool meta  = (e.KeyModifiers & KeyModifiers.Meta)    != 0;
        bool ctrl  = (e.KeyModifiers & KeyModifiers.Control) != 0;
        bool shift = (e.KeyModifiers & KeyModifiers.Shift)   != 0;
        bool alt   = (e.KeyModifiers & KeyModifiers.Alt)     != 0;

        // Clipboard & editor-style shortcuts. Handled BEFORE any
        // scroll-reset so the user can scroll up → select → copy
        // without the view snapping back and invalidating their
        // selection.
        //
        // Modifier convention:
        //  - macOS: ⌘ (Meta) alone — the Apple standard.
        //  - Windows/Linux: two tiers. Ctrl+Shift variants are the
        //    "power-user" gestures (match gnome-terminal / Windows
        //    Terminal). On top of that, plain Ctrl+V always pastes
        //    (Windows Terminal default), and plain Ctrl+C copies the
        //    selection if there is one, falling through to SIGINT
        //    (0x03) otherwise so shells still receive a Ctrl+C break
        //    when no selection exists.
        bool macShortcut      = isMac  && meta && !ctrl;
        bool ctrlShiftEditor  = !isMac && ctrl && shift;
        bool winCtrlOnly      = !isMac && ctrl && !shift && !alt;
        if (macShortcut || ctrlShiftEditor)
        {
            switch (e.Key)
            {
                case Key.V: _ = PasteFromClipboardAsync(); e.Handled = true; return;
                case Key.C: _ = CopySelectionAsync();      e.Handled = true; return;
                case Key.A: _buffer.SelectAll();           e.Handled = true; return;
                case Key.K: _buffer.ClearScrollback();     e.Handled = true; return;
                case Key.F:
                    FindRequested?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    return;

                // Font zoom — +/= increase, -/_ decrease, 0 reset.
                // Key.OemPlus is the `=` key (Shift+= is `+`), so we
                // accept both the plain and shifted variants.
                case Key.OemPlus:
                case Key.Add:
                    AdjustFontSize(+1);              e.Handled = true; return;
                case Key.OemMinus:
                case Key.Subtract:
                    AdjustFontSize(-1);              e.Handled = true; return;
                case Key.D0:
                case Key.NumPad0:
                    ResetFontSize();                 e.Handled = true; return;
            }
        }

        // Plain Ctrl+V on Windows/Linux → paste (Windows Terminal
        // convention — the literal "Ctrl+V character" 0x16 has almost
        // no modern use). Plain Ctrl+C → copy if there's a selection,
        // otherwise fall through so KeyMapper sends SIGINT (0x03).
        if (winCtrlOnly)
        {
            if (e.Key == Key.V)
            {
                _ = PasteFromClipboardAsync();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.C && _buffer.Selection != null)
            {
                _ = CopySelectionAsync();
                _buffer.ClearSelection();
                e.Handled = true;
                return;
            }
        }

        // Shift+PgUp/PgDn page the scrollback viewport without sending
        // the key to the shell. Matches xterm/iTerm2 behaviour. Without
        // Shift, PgUp/PgDn fall through to KeyMapper and reach the shell.
        if (shift && (e.Key == Key.PageUp || e.Key == Key.PageDown))
        {
            int page = Math.Max(1, _buffer.Rows - 1);
            if (e.Key == Key.PageUp) _buffer.ScrollViewUp(page);
            else                     _buffer.ScrollViewDown(page);
            e.Handled = true;
            return;
        }

        // Selection-aware delete. When the user has a mouse selection
        // that ends right at the cursor position (i.e. they just
        // selected characters they typed and pressed Delete or
        // Backspace), translate it into N backspaces sent to the
        // shell so readline actually deletes those characters. If the
        // selection is anywhere else on the line, the shell's line-
        // editor cursor isn't over it and backspaces would delete the
        // wrong text — fall through to the normal key handling in
        // that case and just clear the highlight.
        if ((e.Key == Key.Back || e.Key == Key.Delete) && _buffer.Selection != null)
        {
            int sent = SendBackspacesForSelection();
            if (sent > 0)
            {
                _buffer.ClearSelection();
                _buffer.ResetScrollOffset();
                e.Handled = true;
                return;
            }
            _buffer.ClearSelection();
            // fall through to send the key normally
        }

        var bytes = KeyMapper.Map(e, _buffer.ApplicationCursorKeys, _buffer.ApplicationKeypad);
        if (bytes.Length > 0)
        {
            // Actual shell input — snap to live buffer so the user
            // sees the prompt they're typing into.
            _buffer.ResetScrollOffset();
            RaiseInput(bytes, InputLineOrigin.Typed);
            e.Handled = true;
        }
    }

    /// <summary>If the live-screen selection sits on the cursor's row
    /// at or behind the cursor, send one DEL (0x7F) per character so
    /// the shell's line editor erases them. Returns the number of DEL
    /// bytes sent, or 0 when the selection isn't in a delete-safe
    /// position (different row, starts past the cursor, etc.).</summary>
    private int SendBackspacesForSelection()
    {
        var sel = _buffer.Selection;
        if (sel == null) return 0;
        var (r1, c1, r2, c2) = sel.Normalized();

        // Single-row selections only. We can't translate multi-row
        // selections into backspaces without knowing how long each
        // wrapped segment is in the shell's logical line buffer.
        int cursorAbs = _buffer.VisualToAbsRow(_buffer.CursorRow);
        if (r1 != cursorAbs || r2 != cursorAbs) return 0;

        // The selection has to start before the cursor (there's
        // something to erase) and reach up to or past the cursor
        // (so we're erasing the tail, not a middle slice the
        // shell's line-editor wouldn't line up with). If the user
        // over-dragged into trailing blanks past the cursor we
        // still accept it and just clamp N to the typed portion.
        if (c1 >= _buffer.CursorCol)     return 0;
        if (c2 + 1 < _buffer.CursorCol)  return 0;

        int n = _buffer.CursorCol - c1;
        if (n <= 0) return 0;

        var payload = new byte[n];
        for (int i = 0; i < n; i++) payload[i] = 0x7F; // DEL = shell erase-char
        RaiseInput(payload, InputLineOrigin.Programmatic);
        return n;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        _altHeld = (e.KeyModifiers & KeyModifiers.Alt) != 0;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (string.IsNullOrEmpty(e.Text)) return;
        // Skip control chars — OnKeyDown / KeyMapper already dispatched
        // them (Enter → CR, Tab → 0x09, Backspace → BS/DEL, Escape →
        // ESC). On Windows, Avalonia's TextInput fires alongside
        // KeyDown for keys like Enter, so without this filter we'd
        // double-send every Enter as "\r\r" which cmd.exe renders as
        // two newlines — the "prompt keeps scrolling up" symptom.
        // e.Handled on KeyDown does NOT suppress the subsequent
        // TextInput in Avalonia; they're separate event channels.
        if (e.Text.Length == 1 && e.Text[0] < 0x20) { e.Handled = true; return; }
        _buffer.ResetScrollOffset();
        var bytes = KeyMapper.MapTextInput(e.Text, _altHeld);
        if (bytes.Length > 0) { RaiseInput(bytes, InputLineOrigin.Typed); e.Handled = true; }
    }

    // ---- Mouse ----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        var pos = e.GetPosition(this);

        // Scrollbar drag: left-click inside the right-edge hit zone
        // (wider than the visible bar) starts a scrollbar drag.
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsLeftButtonPressed
            && _buffer.ScrollbackCount > 0
            && pos.X >= Bounds.Width - TerminalRenderer.ScrollbarHitZone)
        {
            _scrollbarDrag = true;
            _buffer.SetScrollOffset(
                TerminalRenderer.YToScrollOffset(pos.Y, _buffer.ScrollbackCount, _buffer.Rows, Bounds.Height));
            ShowScrollbar();
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        var (row, col) = GridPos(pos);
        int btn = props.IsLeftButtonPressed   ? 0
                : props.IsMiddleButtonPressed ? 1
                : props.IsRightButtonPressed  ? 2 : -1;
        if (btn < 0) return;

        _pressedBtn = btn;
        _mouseDown  = true;

        // Click-count tracking for word/line selection.
        var now = DateTime.UtcNow;
        if (now - _lastClickTime <= DoubleClickThreshold
            && row == _lastClickRow && col == _lastClickCol)
            _clickCount++;
        else
            _clickCount = 1;
        _lastClickTime = now; _lastClickRow = row; _lastClickCol = col;

        // Mouse-reporting mode: forward to PTY as SGR (1006) click
        // unless we're viewing scrollback.
        if (_buffer.MouseMode > 0 && _buffer.ScrollOffset == 0)
        {
            SendMouse(btn, row, col, e.KeyModifiers, pressed: true);
            return;
        }

        // OSC 8 hyperlink: single left-click on a linked cell opens the URL.
        if (_clickCount == 1 && btn == 0)
        {
            var cells = _buffer.GetRowForRender(row);
            if (cells != null && col < cells.Length && cells[col].HyperlinkId != 0
                && _buffer.TryGetHyperlink(cells[col].HyperlinkId, out var url))
            {
                HyperlinkClicked?.Invoke(this, url);
                e.Handled = true;
                return;
            }
        }

        if (_clickCount >= 3)
        {
            _buffer.SelectLine(row);
            _selectionPending = false;
        }
        else if (_clickCount == 2)
        {
            _buffer.SelectWord(row, col);
            _selectionPending = false;
        }
        else
        {
            // Single press: drop any previous selection and mark a
            // pending anchor. A real Selection object only materialises
            // when the pointer reaches a different cell (see OnPointerMoved).
            _buffer.ClearSelection();
            _selectionPending = true;
            _pressedRow = row;
            _pressedCol = col;
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);

        if (_scrollbarDrag)
        {
            _buffer.SetScrollOffset(
                TerminalRenderer.YToScrollOffset(pos.Y, _buffer.ScrollbackCount, _buffer.Rows, Bounds.Height));
            ShowScrollbar();
            e.Handled = true;
            return;
        }

        // Hovering the right-edge hit zone keeps the bar visible so
        // the user can grab it without wiggling the wheel first.
        if (pos.X >= Bounds.Width - TerminalRenderer.ScrollbarHitZone
            && _buffer.ScrollbackCount > 0)
        {
            ShowScrollbar();
        }

        var (row, col) = GridPos(pos);

        if (_mouseDown)
        {
            if (_buffer.MouseMode >= 1002 && _buffer.ScrollOffset == 0)
            { SendMouse(_pressedBtn + 32, row, col, e.KeyModifiers, pressed: true); return; }
            if (_buffer.MouseMode == 0 || _buffer.ScrollOffset > 0)
            {
                // First drag movement — materialise the selection
                // anchored at the press position.
                if (_selectionPending && (row != _pressedRow || col != _pressedCol))
                {
                    _buffer.StartSelection(_pressedRow, _pressedCol);
                    _selectionPending = false;
                }
                if (_buffer.Selection != null)
                    _buffer.ExtendSelection(row, col);
            }
        }
        else if (_buffer.MouseMode >= 1003 && _buffer.ScrollOffset == 0)
        {
            SendMouse(35, row, col, e.KeyModifiers, pressed: true); // btn=3 = motion without button
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_scrollbarDrag)
        {
            _scrollbarDrag = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        var (row, col) = GridPos(e.GetPosition(this));
        bool wasDown = _mouseDown;
        _mouseDown = false;

        if (_buffer.MouseMode > 0 && _buffer.ScrollOffset == 0)
        { SendMouse(_pressedBtn, row, col, e.KeyModifiers, pressed: false); return; }

        if (wasDown)
        {
            // Pure click, no drag: _selectionPending is still set.
            // The earlier ClearSelection in OnPointerPressed already
            // cleared any prior selection; we just drop the flag.
            if (_selectionPending)
            {
                _selectionPending = false;
            }
            else if (_buffer.Selection != null)
            {
                _buffer.ExtendSelection(row, col);
                var text = _buffer.GetSelectedText();
                if (!string.IsNullOrEmpty(text)) _ = CopyToClipboardAsync(text);
            }
        }
    }

    // Smooth pixel scroll. Avalonia's PointerWheelEventArgs.Delta.Y
    // is OS-normalised — mouse wheels deliver ±1 per notch, macOS
    // trackpads emit fractional values matching finger motion. We
    // scale by PixelsPerTick (roughly the height of 3 text lines, the
    // Windows default feel) so one notch advances about three rows.
    // The buffer does the fractional accumulation internally via
    // PixelScrollOffset — no integer rounding on our side.
    private const double PixelsPerTick = 40.0;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        // On alt-screen with mouse mode enabled, forward the wheel as a
        // mouse event (apps like less / htop handle scrolling internally).
        if (_buffer.IsAltScreen && _buffer.MouseMode > 0)
        {
            var (row, col) = GridPos(e.GetPosition(this));
            int btn = e.Delta.Y > 0 ? 64 : 65;
            SendMouse(btn, row, col, e.KeyModifiers, pressed: true);
            e.Handled = true;
            return;
        }

        // Positive wheel delta = scroll up (toward scrollback) in pixel
        // units. Buffer clamps at scrollback bounds. Buffer.Changed
        // handler drives the repaint.
        _buffer.ScrollByPixels(e.Delta.Y * PixelsPerTick, _renderer.CellHeight);
        ShowScrollbar();
        e.Handled = true;
    }

    private void SendMouse(int btn, int row, int col, KeyModifiers mods, bool pressed)
    {
        // Always SGR (mode 1006) encoding — unambiguous at any grid
        // size. Apps that only speak the legacy X10 encoding won't
        // receive events but modern ones all handle 1006.
        int b = btn;
        if ((mods & KeyModifiers.Shift)   != 0) b += 4;
        if ((mods & KeyModifiers.Alt)     != 0) b += 8;
        if ((mods & KeyModifiers.Control) != 0) b += 16;
        char fin = pressed ? 'M' : 'm';
        var seq = Encoding.ASCII.GetBytes($"\x1b[<{b};{col + 1};{row + 1}{fin}");
        Input?.Invoke(this, seq);
    }

    private (int row, int col) GridPos(Point p)
    {
        int row = Math.Clamp((int)(p.Y / _renderer.CellHeight), 0, _buffer.Rows - 1);
        int col = Math.Clamp((int)(p.X / _renderer.CellWidth),  0, _buffer.Cols - 1);
        return (row, col);
    }

    // ---- Clipboard ----

    /// <summary>Public façade: copy the current selection (if any) to
    /// the OS clipboard. No-op when nothing is selected.</summary>
    public Task CopySelectionAsync() => CopySelectionAsyncCore();

    /// <summary>Public façade: read the OS clipboard and feed it into
    /// the terminal. Prefers image payloads over text — when an image
    /// is on the clipboard we spill it to a temp file and paste the
    /// path, which is how Claude Code and similar CLIs consume
    /// pasted images on macOS.</summary>
    public Task PasteFromClipboardAsync() => PasteFromClipboardAsyncCore();

    /// <summary>Public façade: select the current viewport.</summary>
    public void SelectAll() => _buffer.SelectAll();

    // ---- Find ----

    /// <summary>Raised when the user hits Cmd+F (Ctrl+Shift+F) so the
    /// host can show a find bar. The host drives search/navigation via
    /// <see cref="Find"/>, <see cref="FindNext"/>, <see cref="FindPrev"/>,
    /// and <see cref="CloseFind"/>.</summary>
    public event EventHandler? FindRequested;

    // Search runs off the UI thread because a 5000-row scrollback takes
    // non-trivial time to scan. Each Find() call cancels any in-flight
    // scan and debounces briefly so rapid typing into the find bar
    // doesn't launch a scan per keystroke.
    private CancellationTokenSource? _searchCts;
    private int _searchGeneration;
    private const int SearchDebounceMs = 120;

    /// <summary>Update the search needle and rebuild the match list.
    /// Pass null or empty to clear. Scans happen on a background thread;
    /// results are applied on the UI thread when ready. Subsequent
    /// calls cancel the previous scan.</summary>
    public void Find(string? needle)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        if (string.IsNullOrEmpty(needle))
        {
            _buffer.ClearSearch();
            return;
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        int gen = ++_searchGeneration;
        _ = RunFindAsync(needle, gen, cts.Token);
    }

    private async Task RunFindAsync(string needle, int gen, CancellationToken ct)
    {
        try
        {
            await Task.Delay(SearchDebounceMs, ct).ConfigureAwait(true);
            if (gen != _searchGeneration) return;

            // Snapshot has to run on the UI thread — it reads the
            // Scrollback ring and live-screen rows which are mutated
            // by PTY writes on the same thread.
            var snapshot = _buffer.SnapshotRows();

            var matches = await Task.Run(
                () => TerminalBuffer.ScanMatches(snapshot, needle, ct),
                ct).ConfigureAwait(true);

            if (ct.IsCancellationRequested || gen != _searchGeneration) return;
            _buffer.ApplySearchResults(needle, matches);
        }
        catch (OperationCanceledException)
        {
            // superseded by a later Find call — nothing to do
        }
        catch (Exception ex)
        {
            TerminalLog.Error($"[TerminalControl] Find failed: {ex.Message}");
        }
    }

    /// <summary>Jump to the next search match.</summary>
    public void FindNext() => _buffer.NextMatch();

    /// <summary>Jump to the previous search match.</summary>
    public void FindPrev() => _buffer.PrevMatch();

    /// <summary>Leave find mode — drops matches and hides highlights.</summary>
    public void CloseFind() => _buffer.ClearSearch();

    /// <summary>Number of matches for the current needle. Useful for a
    /// host-rendered "N of M" counter in the find bar.</summary>
    public int MatchCount => _buffer.SearchMatches.Count;

    /// <summary>1-based index of the current match, or 0 if none.</summary>
    public int CurrentMatch => _buffer.CurrentMatchIndex + 1;

    private async Task CopySelectionAsyncCore()
    {
        var t = _buffer.GetSelectedText();
        if (!string.IsNullOrEmpty(t)) await CopyToClipboardAsync(t);
    }

    private async Task CopyToClipboardAsync(string text)
    {
        var cb = TopLevel.GetTopLevel(this)?.Clipboard;
        if (cb == null) return;
        // DataTransfer is IDisposable — returning the SetDataAsync
        // task directly would let the using/dispose race the set.
        using var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(DataFormat.Text, text));
        await cb.SetDataAsync(transfer);
    }

    // Image-bytes clipboard identifiers for "screenshot to clipboard"
    // captures. TryGetFileAsync already covers Finder / Explorer file
    // copies, so this list is only the raw-bytes variants — macOS UTIs
    // and MIME types, plus the Windows CF_DIB / PNG format names
    // Avalonia surfaces on win32 clipboard.
    private static readonly string[] ImageFormats =
    {
        "public.png",  "image/png",  "PNG",
        "public.tiff", "image/tiff",
        "public.jpeg", "image/jpeg", "JPEG",
        "DeviceIndependentBitmap", "image/bmp", "BMP",
    };

    private async Task PasteFromClipboardAsyncCore()
    {
        var cb = TopLevel.GetTopLevel(this)?.Clipboard;
        if (cb == null) return;

        using var transfer = await cb.TryGetDataAsync();
        if (transfer == null) return;

        // 1. File reference (Finder copy, drag source). Avalonia
        // normalises cross-platform file formats into DataFormat.File.
        var file = await transfer.TryGetFileAsync();
        if (file != null)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path)) { Paste(path); return; }
        }

        // 2. Image bytes (screenshot-to-clipboard). Spill to a temp
        // file and paste the path — CLIs that accept image file
        // arguments can then pick them up just as they would a
        // dragged-in file.
        foreach (var ident in ImageFormats)
        {
            var fmt  = DataFormat.CreateBytesPlatformFormat(ident);
            var data = await transfer.TryGetValueAsync(fmt);
            if (data is { Length: > 0 })
            {
                var path = await WriteClipboardImageToTempAsync(data, ident);
                Paste(path);
                return;
            }
        }

        // 3. Plain text — the common case.
        var t = await transfer.TryGetTextAsync();
        if (!string.IsNullOrEmpty(t)) Paste(t);
    }

    /// <summary>Directory name under the OS temp dir where pasted
    /// images are spilled. Hosts can override to segregate or brand
    /// the path (e.g. an IDE might use its own prefix).</summary>
    public static string PasteImageDirectoryName { get; set; } = "exclr8-terminal-paste";

    /// <summary>Write clipboard image bytes to a temp file and return
    /// its path. The file extension is derived from the clipboard
    /// format so downstream tools can identify the image type
    /// without sniffing. Async so a multi-MB screenshot doesn't stall
    /// the UI thread while the file is written.</summary>
    private static async Task<string> WriteClipboardImageToTempAsync(byte[] data, string format)
    {
        string ext = format switch
        {
            "public.png"  or "image/png"  or "PNG"                         => ".png",
            "public.tiff" or "image/tiff"                                  => ".tiff",
            "public.jpeg" or "image/jpeg" or "JPEG"                        => ".jpg",
            "DeviceIndependentBitmap" or "image/bmp" or "BMP"              => ".bmp",
            _                                                              => ".bin",
        };
        var dir = Path.Combine(Path.GetTempPath(), PasteImageDirectoryName);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"paste-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}{ext}");
        await File.WriteAllBytesAsync(path, data);
        return path;
    }

    // ---- Focus ----

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        _buffer.NotifyFocus(true);
        // NotifyFocus queues a reply via ReplyToPty; drain + forward.
        var replies = _buffer.TakeReplies();
        if (replies != null) Output?.Invoke(this, replies);
        _blinkVisible = true;
        _renderer.BlinkVisible = true;
        InvalidateVisual();
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        _buffer.NotifyFocus(false);
        var replies = _buffer.TakeReplies();
        if (replies != null) Output?.Invoke(this, replies);
        InvalidateVisual();
    }

    // ---- Helpers ----

    private sealed class ActionObserver<T> : IObserver<T>
    {
        private readonly Action<T> _f;
        public ActionObserver(Action<T> f) { _f = f; }
        public void OnCompleted() { }
        public void OnError(Exception e) { }
        public void OnNext(T v) { _f(v); }
    }

    // ---- Disposal ----

    private bool _disposed;

    /// <summary>Stop timers, cancel in-flight search, tear down the
    /// process-tree watcher (kqueue fd / WMI subscription), and detach
    /// from the buffer. Call when a host removes this control from its
    /// layout for good. Idempotent. Re-using a disposed instance is not
    /// supported.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _blinkTimer.Stop();
        _scrollbarTimer.Stop();

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        lock (_processWatchLock) StopWatcher_Locked();

        GC.SuppressFinalize(this);
    }
}
