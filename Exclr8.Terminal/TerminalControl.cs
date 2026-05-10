using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Reactive;
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

    // Synchronized-output (DECSET 2026) state. While the app holds the
    // mode on, we coalesce InvalidateVisual calls — the rendered frame
    // is "atomic" with respect to the byte stream so a half-painted
    // composition (the typical TUI tearing pattern) never reaches the
    // screen. A guard timer caps how long we'll honour the request, so
    // a misbehaving emitter that opens BSU and never closes it can't
    // freeze the display.
    private readonly DispatcherTimer _syncOutputTimer;
    private static readonly TimeSpan SyncOutputMaxHold = TimeSpan.FromMilliseconds(150);

    // Resize debounce. Window-manager drag-resize gestures and tab/
    // pane reparents emit a burst of Bounds changes — sometimes
    // dozens within a single frame — and reflowing on every one
    // makes the buffer thrash and the PTY receive a SIGWINCH storm.
    // The debounce coalesces the burst: each Bounds change schedules
    // a deferred resize, restarting the timer; only the FINAL size
    // (after activity stops) reaches the buffer + the Resized event.
    private readonly DispatcherTimer _resizeDebounceTimer;
    private (int Cols, int Rows)? _pendingResize;
    private static readonly TimeSpan ResizeDebounceDelay = TimeSpan.FromMilliseconds(50);

    // Drag-select auto-scroll. When the user drag-selects past the
    // top or bottom edge of the viewport, scroll the viewport in
    // that direction and extend the selection to the edge so the
    // selection grows with the scroll. Uses a timer because the
    // pointer can be HELD outside the viewport without moving —
    // OnPointerMoved would never fire again, but we still want the
    // viewport to scroll as long as the button stays down.
    private readonly DispatcherTimer _dragAutoScrollTimer;
    private Point _lastDragPos;
    private static readonly TimeSpan DragAutoScrollInterval = TimeSpan.FromMilliseconds(35);

    /// <summary>User typed — payload is the byte sequence ready for the
    /// PTY writer.</summary>
    public event EventHandler<ReadOnlyMemory<byte>>? Input;

    /// <summary>Terminal wants bytes sent back to the PTY (DSR / DA
    /// replies). The consumer forwards to <c>pty.WriterStream</c>.</summary>
    public event EventHandler<ReadOnlyMemory<byte>>? Output;

    /// <summary>Cell grid dimensions changed.</summary>
    public event EventHandler<(int Cols, int Rows)>? Resized;

    /// <summary>User clicked an OSC 8 hyperlink OR a span produced
    /// by a registered <see cref="ILinkProvider"/>. Only URLs that
    /// pass <see cref="LinkActivationPolicy"/> reach this event;
    /// blocked clicks fire <see cref="LinkBlocked"/> instead.</summary>
    public event EventHandler<string>? HyperlinkClicked;

    /// <summary>A hyperlink click was rejected by
    /// <see cref="LinkActivationPolicy"/>. Hosts can surface a toast
    /// or log it; the URL has already been dropped.</summary>
    public event EventHandler<string>? LinkBlocked;

    /// <summary>Predicate that decides whether a clicked URL is
    /// allowed to surface as <see cref="HyperlinkClicked"/>. Default
    /// allows only <c>http://</c> and <c>https://</c> — OSC 8 can
    /// ship arbitrary schemes (<c>javascript:</c>, <c>file://</c>,
    /// <c>vbs://</c>, …) and custom <see cref="ILinkProvider"/>s
    /// can return anything, so the host has to opt in to wider
    /// schemes explicitly. Set to <c>_ =&gt; true</c> to allow
    /// everything.</summary>
    public Func<string, bool> LinkActivationPolicy { get; set; } = DefaultLinkPolicy;

    private static bool DefaultLinkPolicy(string url) =>
        url.StartsWith("http://",  StringComparison.OrdinalIgnoreCase)
     || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Run a clicked URL through the policy and either
    /// raise <see cref="HyperlinkClicked"/> or
    /// <see cref="LinkBlocked"/>. Centralised so OSC 8 and link-
    /// provider paths share the same gate.</summary>
    private void ActivateLink(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        if (LinkActivationPolicy(url))
            HyperlinkClicked?.Invoke(this, url);
        else
            LinkBlocked?.Invoke(this, url);
    }

    // Registered ILinkProviders. The renderer asks each per visible
    // row, on every frame; providers are expected to be cheap (regex
    // per row, no allocations beyond the matches themselves).
    //
    // Immutable + atomic swap: register/dispose can happen on any
    // thread (provider disposable returned to the host gets called
    // wherever the host disposes it), and the renderer + pointer
    // handler both iterate on the UI thread. A plain List would race
    // on concurrent Add/Remove vs. iteration. ImmutableList swaps
    // cheaply and gives every reader a consistent snapshot.
    private ImmutableList<ILinkProvider> _linkProviders = ImmutableList<ILinkProvider>.Empty;
    public IReadOnlyList<ILinkProvider> LinkProviders => _linkProviders;

    /// <summary>Register a custom link matcher. Returns a disposable
    /// that detaches the provider. Most-recent registration wins
    /// when ranges overlap.</summary>
    public IDisposable RegisterLinkProvider(ILinkProvider provider)
    {
        if (provider == null) throw new ArgumentNullException(nameof(provider));
        ImmutableList<ILinkProvider> prev, next;
        do
        {
            prev = _linkProviders;
            next = prev.Add(provider);
        } while (System.Threading.Interlocked.CompareExchange(ref _linkProviders, next, prev) != prev);
        InvalidateVisual();
        return new ProviderRegistration(() =>
        {
            ImmutableList<ILinkProvider> p, n;
            do
            {
                p = _linkProviders;
                n = p.Remove(provider);
                if (ReferenceEquals(p, n)) return; // already removed
            } while (System.Threading.Interlocked.CompareExchange(ref _linkProviders, n, p) != p);
            InvalidateVisual();
        });
    }

    private sealed class ProviderRegistration : IDisposable
    {
        private Action? _dispose;
        public ProviderRegistration(Action dispose) { _dispose = dispose; }
        public void Dispose() { var d = _dispose; _dispose = null; d?.Invoke(); }
    }

    /// <summary>BEL (0x07) — the shell rang the bell. Host decides
    /// what to do (audible beep, visual flash, desktop notification,
    /// or ignore). Fires per BEL byte; consumers typically debounce.</summary>
    public event EventHandler? Bell
    {
        add    => _buffer.Bell += value;
        remove => _buffer.Bell -= value;
    }

    /// <summary>Cursor moved during a Write. Fires once per Write call
    /// — not once per cursor mutation — so high-rate animations don't
    /// flood subscribers.</summary>
    public event EventHandler<(int Row, int Col)>? CursorMoved
    {
        add    => _buffer.CursorMoved += value;
        remove => _buffer.CursorMoved -= value;
    }

    /// <summary>Scroll offset changed. Useful for hosts that drive a
    /// custom scrollbar or "X lines back" indicator.</summary>
    public event EventHandler<int>? ScrollChanged
    {
        add    => _buffer.ScrollChanged += value;
        remove => _buffer.ScrollChanged -= value;
    }

    /// <summary>Selection set, extended, or cleared. New value is null
    /// when cleared.</summary>
    public event EventHandler<TerminalSelection?>? SelectionChanged
    {
        add    => _buffer.SelectionChanged += value;
        remove => _buffer.SelectionChanged -= value;
    }

    /// <summary>OSC 0 / OSC 2 — window title set by the shell.</summary>
    public event EventHandler<string>? TitleChanged
    {
        add    => _buffer.TitleChanged += value;
        remove => _buffer.TitleChanged -= value;
    }

    /// <summary>OSC 0 / OSC 1 — icon name. Most shells use OSC 0 which
    /// sets both title and icon name simultaneously.</summary>
    public event EventHandler<string>? IconNameChanged
    {
        add    => _buffer.IconNameChanged += value;
        remove => _buffer.IconNameChanged -= value;
    }

    /// <summary>OSC 7 — shell-announced current working directory.
    /// Hosts subscribe to this to drive "open new tab here" UX,
    /// session recall, or breadcrumbs.</summary>
    public event EventHandler<string>? WorkingDirectoryChanged
    {
        add    => _buffer.WorkingDirectoryChanged += value;
        remove => _buffer.WorkingDirectoryChanged -= value;
    }

    /// <summary>Last working directory the shell announced (OSC 7).
    /// Null until the shell emits one.</summary>
    public string? WorkingDirectory => _buffer.WorkingDirectory;

    /// <summary>OSC 133 — FinalTerm/iTerm2 semantic prompt markers
    /// (PromptStart/PromptEnd/CommandStart/CommandEnd + exit code).
    /// Hosts use these to draw command-status gutters, jump-to-prompt
    /// nav, and AI-style command boundaries.</summary>
    public event EventHandler<SemanticPromptEventArgs>? SemanticPrompt
    {
        add    => _buffer.SemanticPrompt += value;
        remove => _buffer.SemanticPrompt -= value;
    }

    /// <summary>OSC 9 ; 4 — taskbar / dock-badge progress reporting
    /// from the running command. State + optional 0..100 percentage.
    /// Pairs naturally with <see cref="SemanticPrompt"/> for full
    /// build/test/install lifecycle UX.</summary>
    public event EventHandler<ProgressEventArgs>? ProgressChanged
    {
        add    => _buffer.ProgressChanged += value;
        remove => _buffer.ProgressChanged -= value;
    }

    /// <summary>OSC 52 — shell asked to write to the OS clipboard.
    /// Only fires when <see cref="AllowClipboardAccess"/> is true; the
    /// host decides whether to honour the request.</summary>
    public event EventHandler<ClipboardRequestEventArgs>? ClipboardRequested
    {
        add    => _buffer.ClipboardRequested += value;
        remove => _buffer.ClipboardRequested -= value;
    }

    /// <summary>OSC 52 routing gate. False (default) silently drops
    /// shell-initiated clipboard writes; true raises
    /// <see cref="ClipboardRequested"/>.</summary>
    public bool AllowClipboardAccess
    {
        get => _buffer.AllowClipboardAccess;
        set => _buffer.AllowClipboardAccess = value;
    }

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
    // Recently-emitted exit pids, used to suppress duplicate
    // ProcessTreeChangeKind.Exited events. Both Windows (WMI's
    // deletion filter matching either ParentProcessId or ProcessId)
    // and the kqueue chain-watch pattern can surface a process's
    // death from two angles — its own watcher and its parent's.
    //
    // Bounded at ExitPidMemory with FIFO eviction. The earlier
    // wholesale-clear-on-overflow let every in-flight WMI deletion
    // duplicate slip through immediately after each clear, since the
    // empty set's first Add for any pid succeeds. Single-oldest
    // eviction keeps every other entry's dup-suppression intact.
    private readonly HashSet<int>  _recentlyExitedPids   = new();
    private readonly Queue<int>    _recentlyExitedOrder  = new();
    private const int ExitPidMemory = 1024;
    private IProcessChildWatcher? _processWatcher;
    private int _rootProcessId;
    private Action<ProcessTreeChange>? _processTreeChangedInner;

    /// <summary>Live subscriber count derived from the delegate's
    /// invocation list. Tracking this via a separate counter let
    /// repeated <c>-=</c>'s of the same handler drive the count
    /// negative, which would prevent a future legitimate add from
    /// starting the watcher. Reading it from the delegate keeps the
    /// two in sync by construction.</summary>
    private int ProcessTreeSubscriberCount =>
        _processTreeChangedInner?.GetInvocationList().Length ?? 0;

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
                EnsureWatcherStarted_Locked();
            }
        }
        remove
        {
            if (value == null) return;
            IProcessChildWatcher? toDispose = null;
            lock (_processWatchLock)
            {
                _processTreeChangedInner -= value;
                if (ProcessTreeSubscriberCount == 0) toDispose = StopWatcher_Locked();
            }
            // Dispose outside the lock: WMI's ManagementEventWatcher.Stop()
            // synchronously waits for any in-flight EventArrived handler,
            // which itself takes _processWatchLock — holding the lock
            // here would deadlock the UI thread on a fork/exit racing
            // with this remove.
            if (toDispose != null) try { toDispose.Dispose(); } catch { }
        }
    }

    private void EnsureWatcherStarted_Locked()
    {
        if (_processWatcher != null) return;
        if (ProcessTreeSubscriberCount == 0) return;
        try
        {
            var w = ProcessChildWatcherFactory.Create();
            w.TreeChanged += OnWatcherTreeChanged;
            _processWatcher = w;
            if (_rootProcessId > 0) Watch_Locked(_rootProcessId);
        }
        catch (Exception ex)
        {
            TerminalLog.Error($"[TerminalControl] process-watcher start failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Detaches the watcher from the control's bookkeeping under the
    /// caller's <see cref="_processWatchLock"/> and returns it so the
    /// caller can <c>Dispose()</c> it OUTSIDE the lock. Disposing
    /// inside the lock can deadlock — see the call sites for the
    /// reason.
    /// </summary>
    private IProcessChildWatcher? StopWatcher_Locked()
    {
        var w = _processWatcher;
        _processWatcher = null;
        _watchedPids.Clear();
        // Drop the recent-exit memory too — the watcher is gone, and
        // a future session shouldn't inherit stale suppression for
        // pids the OS might reuse.
        RecentlyExitedClear_Locked();
        // Subscriber count is derived from
        // _processTreeChangedInner.GetInvocationList(); we don't
        // touch the delegate here, so the count stays accurate.
        if (w == null) return null;
        w.TreeChanged -= OnWatcherTreeChanged;
        return w;
    }

    private void Watch_Locked(int pid)
    {
        if (pid <= 0) return;
        // Whenever we start tracking a pid — root-pid set, chain-watch
        // of a Created child, re-targeting after RootProcessId
        // change — purge any stale "recently exited" memory for that
        // pid. Without this, a reused root pid could have its next
        // exit suppressed as a dup (root pids never come through the
        // Created-event path that normally clears the entry).
        _recentlyExitedPids.Remove(pid); // queue ghost is harmless; cleared on its turn
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
        // Re-targeting to a new root — start the dup-suppression
        // memory fresh so a pid from the previous session can't
        // silently mask an exit in the new one.
        RecentlyExitedClear_Locked();
    }

    /// <summary>FIFO-ordered insert into the recently-exited dedup
    /// memory. Returns true on a new entry (caller proceeds), false on
    /// a duplicate (caller suppresses the event). Evicts the
    /// genuinely oldest entry on overflow rather than wholesale
    /// clearing the set, which preserved every other entry's dup
    /// suppression.</summary>
    private bool RecentlyExitedAdd_Locked(int pid)
    {
        if (!_recentlyExitedPids.Add(pid)) return false;
        _recentlyExitedOrder.Enqueue(pid);
        // Evict from the head until live count is back inside the cap.
        // The queue may contain "ghost" pids (Watch_Locked-cleared
        // entries that we left in the queue for cheapness); skip over
        // them — set.Remove returning false costs nothing.
        while (_recentlyExitedPids.Count > ExitPidMemory
               && _recentlyExitedOrder.Count > 0)
        {
            int oldest = _recentlyExitedOrder.Dequeue();
            _recentlyExitedPids.Remove(oldest);
        }
        // Ghost entries can otherwise grow the queue unboundedly when
        // many Watch_Locked calls churn pids without the queue's head
        // ever reaching them. Cap with a 2x safety margin.
        if (_recentlyExitedOrder.Count > ExitPidMemory * 2)
        {
            int target = ExitPidMemory;
            while (_recentlyExitedOrder.Count > target)
            {
                int p = _recentlyExitedOrder.Dequeue();
                _recentlyExitedPids.Remove(p);
            }
        }
        return true;
    }

    private void RecentlyExitedClear_Locked()
    {
        _recentlyExitedPids.Clear();
        _recentlyExitedOrder.Clear();
    }

    /// <summary>Watcher callback. Fires on a backend thread (kqueue
    /// pump on macOS, WMI callback pool on Windows). We do the
    /// watch-bookkeeping synchronously here (chain-watch new children,
    /// forget exited ones) so fast fork/exit races don't slip through,
    /// then marshal the public event onto the Avalonia dispatcher —
    /// subscribers almost certainly touch UI state (badges, menus,
    /// session tags).</summary>
    private void OnWatcherTreeChanged(ProcessTreeChange change)
    {
        if (change.Kind == ProcessTreeChangeKind.Created)
        {
            // Watch_Locked clears the "recently exited" entry for
            // this pid as part of its normal setup, so a pid reused
            // after an earlier exit isn't silently suppressed here.
            lock (_processWatchLock) Watch_Locked(change.Pid);
        }
        else // Exited
        {
            bool duplicate;
            lock (_processWatchLock)
            {
                _watchedPids.Remove(change.Pid);
                duplicate = !RecentlyExitedAdd_Locked(change.Pid);
            }
            if (duplicate) return;
        }

        if (_processTreeChangedInner == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            try { _processTreeChangedInner?.Invoke(change); }
            catch (Exception ex)
            {
                TerminalLog.Error(
                    $"[TerminalControl] ProcessTreeChanged {change.Kind} dispatch: {ex.Message}");
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

    /// <summary>When true (default), OSC 8 hyperlink cells get a thin
    /// underline drawn beneath them so the user can tell which spans
    /// are clickable. Hosts that draw their own button-style links
    /// (filled bg, contrasting fg, distinct from surrounding text)
    /// can flip this off — the underline sits on the bottom of the
    /// cell and competes visually with their styling.</summary>
    public bool ShowHyperlinkUnderline
    {
        get => _renderer.ShowHyperlinkUnderline;
        set { _renderer.ShowHyperlinkUnderline = value; InvalidateVisual(); }
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
        _buffer.Changed += OnBufferChanged;
        _buffer.SynchronizedOutputChanged += OnSynchronizedOutputChanged;
        _buffer.PaletteChanged += OnPaletteChanged;
        _syncOutputTimer = new DispatcherTimer { Interval = SyncOutputMaxHold };
        _syncOutputTimer.Tick += (_, _) =>
        {
            // Safety net: an app that opened BSU but never sent ESU
            // shouldn't be able to freeze us. Force-flush + stop.
            _syncOutputTimer.Stop();
            InvalidateVisual();
        };

        _resizeDebounceTimer = new DispatcherTimer { Interval = ResizeDebounceDelay };
        _resizeDebounceTimer.Tick += (_, _) => ApplyPendingResize();

        _dragAutoScrollTimer = new DispatcherTimer { Interval = DragAutoScrollInterval };
        _dragAutoScrollTimer.Tick += (_, _) => DragAutoScrollTick();

        this.GetObservable(BoundsProperty)
            .Subscribe(new AnonymousObserver<Rect>(_ => RecomputeGrid()));

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _blinkTimer.Tick += (_, _) =>
        {
            _blinkVisible          = !_blinkVisible;
            _renderer.BlinkVisible = _blinkVisible;
            // Only repaint when something actually blinks: a blink-style
            // cursor, or a cell laid down under SGR 5/6 (tracked via
            // TerminalBuffer.HasBlinkContent — sticky-set in Print,
            // cleared on RIS/DECSTR). Lets an idle terminal that never
            // uses blink skip the 2 Hz full-render entirely.
            var s = _buffer.CursorStyle;
            bool cursorBlinks = _buffer.CursorVisible &&
                s is CursorStyle.BlockBlink or CursorStyle.UnderlineBlink or CursorStyle.BarBlink;
            if (cursorBlinks || _buffer.HasBlinkContent) InvalidateVisual();
        };
        _blinkTimer.Start();

        _scrollbarTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _scrollbarTimer.Tick += OnScrollbarTick;
    }

    private void OnBufferChanged(object? sender, EventArgs e)
    {
        // Hold off rendering while the app has DECSET 2026 active — the
        // guard timer (or matching DECRST) will trigger the actual
        // InvalidateVisual.
        if (_buffer.SynchronizedOutput) return;
        InvalidateVisual();
    }

    private void OnSynchronizedOutputChanged(object? sender, bool on)
    {
        if (on)
        {
            _syncOutputTimer.Stop();
            _syncOutputTimer.Start();
        }
        else
        {
            _syncOutputTimer.Stop();
            InvalidateVisual();
        }
    }

    private void OnPaletteChanged(object? sender, EventArgs e) => InvalidateVisual();

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
    //
    // Producer threads (the PTY reader, an SSH channel, a replay
    // pump) can flood Write() at thousands of chunks per second on
    // verbose output (large `cat`, `find /`, build logs). Each chunk
    // used to do its own Dispatcher.UIThread.Post + parse + bump,
    // which serialises through the dispatcher and starves input/
    // render. The coalescing queue collapses the burst:
    //
    //   * Producer enqueues bytes into a ConcurrentQueue.
    //   * One Post is scheduled per "drain pass", arbitrated by a
    //     CAS on _drainScheduled — concurrent producers all enqueue
    //     but only the first one through schedules a drain.
    //   * Drain runs on the UI thread, dequeues every queued chunk,
    //     parses them in one parser pass per chunk, then yields
    //     control back to Avalonia (re-Post a continuation) every
    //     ~16ms so input and render don't starve mid-drain.
    //
    // With this in place, a 100 MB log dump turns into ~6 dispatcher
    // posts instead of one per chunk.

    private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _writeQueue = new();
    private long _writeQueuedBytes;
    private int  _drainScheduled; // 0 = idle, 1 = drain in flight
    private static readonly TimeSpan DrainYieldBudget = TimeSpan.FromMilliseconds(16);

    /// <summary>How a runaway producer is throttled. Default
    /// <see cref="WriteDropPolicy.None"/> means the queue grows
    /// unbounded — fine for trusted PTYs. Hosts that need a hard
    /// cap (untrusted byte source, embedded device with limited
    /// memory) set <see cref="WriteDropPolicy.OldestFirst"/> + a
    /// non-zero <see cref="WriteQueueMaxBytes"/>.</summary>
    public WriteDropPolicy WriteDropPolicy { get; set; } = WriteDropPolicy.None;

    /// <summary>Cap on bytes held in the pending-write queue when
    /// <see cref="WriteDropPolicy"/> is not None. 0 = unlimited.</summary>
    public long WriteQueueMaxBytes { get; set; }

    /// <summary>Bytes currently waiting in the write queue. Hosts can
    /// surface this as a "back-pressure" indicator. Reads are
    /// approximate when producer threads are racing the UI drain.</summary>
    public long QueuedBytes => System.Threading.Interlocked.Read(ref _writeQueuedBytes);

    /// <summary>Total bytes the drop policy has discarded since the
    /// control was constructed. Stays at 0 with the default
    /// <see cref="WriteDropPolicy.None"/>.</summary>
    public long DroppedBytes { get; private set; }

    /// <summary>Feed bytes from your byte source (PTY, SSH channel,
    /// replay stream, …) into the terminal. Safe to call from any
    /// thread. Bursts from a single producer or from many concurrent
    /// producers are coalesced into a single drain pass on the UI
    /// thread.</summary>
    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        var copy = bytes.ToArray();
        EnqueueAndScheduleDrain(copy);
    }

    public void Write(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return;
        // Take the array as-is when the caller's already given us an
        // owned array — saves a copy on the hot path.
        EnqueueAndScheduleDrain(bytes);
    }

    private void EnqueueAndScheduleDrain(byte[] payload)
    {
        // Honour the drop policy first so we don't enqueue bytes we'd
        // immediately discard. OldestFirst drops *queued* bytes (not
        // the current payload) — preserving freshness, matching how
        // `journalctl --output-fields="..."` and other ring-style
        // sinks behave.
        if (WriteDropPolicy == WriteDropPolicy.OldestFirst
            && WriteQueueMaxBytes > 0)
        {
            // Oversized single payload: a hostile or runaway producer
            // can bypass the cap entirely with one chunk larger than
            // WriteQueueMaxBytes (target goes negative, the drain loop
            // empties the queue, but the unconditional enqueue below
            // stores the whole oversized payload). Trim from the head
            // and keep the freshest tail — same "newest wins" policy
            // we apply to the queued chunks.
            if (payload.Length > WriteQueueMaxBytes)
            {
                int keep = (int)WriteQueueMaxBytes;
                long dropFromHead = payload.Length - keep;
                var trimmed = new byte[keep];
                System.Buffer.BlockCopy(payload, payload.Length - keep, trimmed, 0, keep);
                DroppedBytes += dropFromHead;
                payload = trimmed;
            }

            long target = WriteQueueMaxBytes - payload.Length;
            while (System.Threading.Interlocked.Read(ref _writeQueuedBytes) > target
                   && _writeQueue.TryDequeue(out var dropped))
            {
                System.Threading.Interlocked.Add(ref _writeQueuedBytes, -dropped.Length);
                DroppedBytes += dropped.Length;
            }
        }

        _writeQueue.Enqueue(payload);
        System.Threading.Interlocked.Add(ref _writeQueuedBytes, payload.Length);

        // CAS 0→1: only the first thread through schedules a drain.
        // Subsequent producers see _drainScheduled==1 and just
        // enqueue; the running drain will pick up their bytes.
        if (System.Threading.Interlocked.CompareExchange(ref _drainScheduled, 1, 0) == 0)
        {
            if (Dispatcher.UIThread.CheckAccess()) DrainOnUi();
            else                                   Dispatcher.UIThread.Post(DrainOnUi);
        }
    }

    private void DrainOnUi()
    {
        // Disposal might have run between Post() and the dispatcher
        // picking us up. Drop any leftover queued bytes — the buffer
        // is gone, and parsing them would just churn for no reason.
        if (_disposed)
        {
            while (_writeQueue.TryDequeue(out _)) { }
            System.Threading.Interlocked.Exchange(ref _writeQueuedBytes, 0);
            System.Threading.Interlocked.Exchange(ref _drainScheduled, 0);
            return;
        }

        var deadline = DateTime.UtcNow + DrainYieldBudget;
        try
        {
            while (_writeQueue.TryDequeue(out var chunk))
            {
                System.Threading.Interlocked.Add(ref _writeQueuedBytes, -chunk.Length);
                _buffer.Write(chunk);
                var replies = _buffer.TakeReplies();
                if (replies != null) Output?.Invoke(this, replies);

                // If the drain has been running long enough that
                // input / render would feel sluggish, yield back to
                // Avalonia by re-posting the drain continuation. The
                // CAS flag stays at 1 so producers don't double-
                // schedule.
                if (DateTime.UtcNow >= deadline && !_writeQueue.IsEmpty)
                {
                    if (_buffer.Revision != _lastRevision) InvalidateVisual();
                    Dispatcher.UIThread.Post(DrainOnUi);
                    return;
                }
            }
            if (_buffer.Revision != _lastRevision) InvalidateVisual();
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _drainScheduled, 0);
            // A producer might have enqueued between our last
            // TryDequeue and the flag flip — re-check and reschedule
            // if needed so the new bytes don't wait for the next
            // arbitrary trigger.
            if (!_writeQueue.IsEmpty
                && System.Threading.Interlocked.CompareExchange(ref _drainScheduled, 1, 0) == 0)
            {
                Dispatcher.UIThread.Post(DrainOnUi);
            }
        }
    }

    /// <summary>Hard cap on paste payload size in bytes. Anything
    /// larger is rejected — <see cref="PasteRejected"/> fires so
    /// the host can show a UI prompt; the actual paste is dropped.
    ///
    /// <para>Default 50 MB — comfortably covers <c>pg_dump</c> of a
    /// medium DB, large log tails, generated SQL / JSON, AI-context
    /// content. The cap is mostly a "you definitely fat-fingered
    /// the wrong clipboard" guard rather than a meaningful limit
    /// for legitimate use. Set to <see cref="int.MaxValue"/> for no
    /// effective cap.</para>
    /// </summary>
    public int PasteMaxBytes { get; set; } = 50 * 1024 * 1024;

    /// <summary>Fires when a paste was rejected for being larger
    /// than <see cref="PasteMaxBytes"/>. Argument is the size of
    /// the rejected paste in bytes. Hosts use this to show a
    /// "paste too large (X MB / max Y MB)" toast or confirm dialog.
    /// Without subscribing the cap is silent — the paste does
    /// nothing and the user has no way to know why.</summary>
    public event EventHandler<long>? PasteRejected;

    /// <summary>Bytes per chunk when forwarding a paste to the
    /// <see cref="Input"/> event. <b>Default 0 — chunking off.</b>
    /// The whole payload fires as one <see cref="Input"/> event,
    /// matching pre-1.0.2 behaviour and avoiding host-side write
    /// races (see below).
    ///
    /// <para><b>When to enable.</b> Set to a positive value only if
    /// your host's <see cref="Input"/> handler genuinely cannot
    /// accept a large single write — e.g., a transport that frames
    /// at a fixed size, or a slow consumer that times out paste
    /// mode if it doesn't see the close marker promptly. 4096 is a
    /// reasonable starting point (matches Windows ConPTY's input
    /// pipe buffer).</para>
    ///
    /// <para><b>Important — host-side serialisation.</b> Once
    /// chunking is on we fire multiple <see cref="Input"/> events
    /// in quick succession. If your handler does
    /// <c>async (_, p) =&gt; await pty.WriteAsync(p)</c> with no
    /// serialisation, two writes will race on the same handle —
    /// <see cref="System.IO.Stream.WriteAsync(byte[],int,int)"/>
    /// is not thread-safe under concurrent calls and bytes will
    /// interleave or get lost. Wrap the writer in a queue / lock /
    /// SemaphoreSlim before turning chunking on.</para>
    /// </summary>
    public int PasteChunkSize { get; set; }

    /// <summary>Optional delay between paste chunks, in
    /// milliseconds. Only relevant when
    /// <see cref="PasteChunkSize"/> &gt; 0. 0 (default) yields to
    /// the dispatcher between chunks but doesn't sleep. Set to a
    /// positive value (1–10 ms) if the consumer needs more breathing
    /// room between chunks.</summary>
    public int PasteChunkDelayMs { get; set; }

    /// <summary>
    /// Paste text into the terminal. Wraps in <c>ESC[200~</c> /
    /// <c>ESC[201~</c> when DECSET 2004 (bracketed paste) is active —
    /// lets the shell distinguish typed vs pasted input.
    ///
    /// <para>The clipboard bytes are forwarded verbatim — NULs and
    /// other control bytes are not stripped, matching iTerm2 /
    /// Terminal.app behaviour. Consumers (shells, TUIs) decide what
    /// to do with non-printable content.</para>
    ///
    /// <para>One exception: when bracketed paste is active, any
    /// embedded <c>ESC [ 2 0 1 ~</c> close-marker in the body is
    /// scrubbed. Without this, a paste containing the close marker
    /// (either accidentally — e.g., copying terminal output that
    /// previously contained the marker — or maliciously) would
    /// prematurely terminate paste mode and the rest of the bytes
    /// would land as if the user had typed them, including any
    /// embedded newlines that submit the partial input. The user-
    /// visible symptom is "the paste cut off and the rest appeared
    /// somewhere weird." Replacing the marker with the open-marker
    /// (<c>ESC [ 2 0 0 ~</c>) keeps paste mode open and the bytes
    /// flowing; downstream apps see no functional difference because
    /// the close-marker is a paste-protocol primitive, never part
    /// of legitimate user content.</para>
    /// </summary>
    public void Paste(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Paste is about to push PTY bytes that will move the cursor
        // and almost certainly paint over wherever the selection was.
        // Drop the selection now so the stale highlight doesn't linger.
        _buffer.ClearSelection();

        int innerBytes = Encoding.UTF8.GetByteCount(text);
        if (innerBytes > PasteMaxBytes)
        {
            PasteRejected?.Invoke(this, innerBytes);
            return;
        }

        byte[] payload;
        if (_buffer.BracketedPaste)
        {
            // ESC[200~ text ESC[201~. Encode UTF-8 directly into the
            // payload slice between the start/end markers — one byte[]
            // allocation instead of two (intermediate GetBytes + a
            // wrap-copy). Do NOT translate CR inside the brackets —
            // bracketed paste intentionally lets the shell see raw
            // newlines so it can decide how to handle them (often, as
            // input separators).
            payload = new byte[innerBytes + 12];
            "\x1b[200~"u8.CopyTo(payload);
            Encoding.UTF8.GetBytes(text, payload.AsSpan(6, innerBytes));
            "\x1b[201~"u8.CopyTo(payload.AsSpan(6 + innerBytes));

            // Scrub any embedded close marker in the body. We only
            // touch bytes inside the brackets [6, 6+innerBytes); the
            // framing markers themselves stay intact.
            ScrubEmbeddedPasteClose(payload.AsSpan(6, innerBytes));
        }
        else
        {
            payload = Encoding.UTF8.GetBytes(text);
        }

        // For small pastes (under one chunk) send in one shot —
        // matches the legacy behaviour and avoids the async overhead
        // for short pastes which are the common case.
        int chunkSize = PasteChunkSize;
        if (chunkSize <= 0 || payload.Length <= chunkSize)
        {
            RaiseInput(payload, InputLineOrigin.Pasted);
            return;
        }

        // Large paste: chunk + yield between chunks so the consumer
        // can drain its read pipe. Fire-and-forget; the UI thread
        // doesn't wait. Reading the chunked-send method end-to-end:
        // the FIRST chunk carries the \e[200~ open-marker; the LAST
        // carries \e[201~; each chunk is a contiguous slice of the
        // already-built payload, so brackets never straddle a chunk
        // boundary.
        _ = SendPasteChunkedAsync(payload, chunkSize, PasteChunkDelayMs);
    }

    private async Task SendPasteChunkedAsync(byte[] payload, int chunkSize, int delayMs)
    {
        try
        {
            int offset = 0;
            while (offset < payload.Length)
            {
                int len = Math.Min(chunkSize, payload.Length - offset);
                var chunk = new byte[len];
                Array.Copy(payload, offset, chunk, 0, len);
                RaiseInput(chunk, InputLineOrigin.Pasted);
                offset += len;
                if (offset >= payload.Length) break;
                if (delayMs > 0)
                    await Task.Delay(delayMs).ConfigureAwait(true);
                else
                    // Yield to the dispatcher so the host's PTY writer
                    // can drain its pipe between chunks. Without the
                    // yield we'd hammer Input?.Invoke in a tight loop
                    // on the UI thread and the consumer would never
                    // get a chance to read.
                    await Task.Yield();
            }
        }
        catch (Exception ex)
        {
            TerminalLog.Error($"[TerminalControl] paste chunked-send failed: {ex.Message}");
        }
    }

    /// <summary>Replace any <c>ESC [ 2 0 1 ~</c> sequence inside a
    /// bracketed-paste body with <c>ESC [ 2 0 0 ~</c>. The 6-byte
    /// pattern is unambiguous (no overlapping shorter prefix that
    /// looks like a continuation) so a single linear scan suffices.
    /// </summary>
    private static void ScrubEmbeddedPasteClose(Span<byte> body)
    {
        // ESC [ 2 0 1 ~  →  ESC [ 2 0 0 ~  (just flip the '1' to '0')
        for (int i = 0; i + 5 < body.Length; i++)
        {
            if (body[i]     == 0x1B  // ESC
             && body[i + 1] == (byte)'['
             && body[i + 2] == (byte)'2'
             && body[i + 3] == (byte)'0'
             && body[i + 4] == (byte)'1'
             && body[i + 5] == (byte)'~')
            {
                body[i + 4] = (byte)'0';
                i += 5; // skip past the rewritten sequence
            }
        }
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        // Snapshot once: a concurrent register/dispose mid-render
        // would otherwise let Count and indexed access disagree.
        var providers = _linkProviders;
        _renderer.Render(ctx, _buffer, Bounds.Size, IsFocused, _colorScheme,
            providers.Count > 0 ? providers : null);
        _lastRevision = _buffer.Revision;
    }

    // Minimum grid size we'll honour. Anything smaller is almost
    // certainly a transient layout pass (e.g. mid-reparent after a
    // MoveCell) — resizing to those dimensions would shove live-screen
    // rows into scrollback and trigger a SIGWINCH redraw from the
    // shell, which looks to the user like the history got duplicated.
    private const int MinUsableCols = 10;
    private const int MinUsableRows = 3;

    /// <summary>Pixel deadband around each cell-grid integer boundary.
    /// Bounds wobbles smaller than this don't flip the grid by one
    /// cell. Sized to absorb common host-side jitter sources — a
    /// focus-ring border-thickness change of 1–2 px per side, font-
    /// hinting nudges, scrollbar fade-in/out 1 px reserved space.
    /// Without it: a 2 px Bounds change crosses an integer cell
    /// boundary, RecomputeGrid emits a Resized event, the host
    /// forwards to ConPTY, and ConPTY reframes the screen — which
    /// on Windows + cmd.exe collapses cursor-traversed-but-unwritten
    /// rows (the blanks from `echo.`) because the console screen
    /// buffer can't distinguish them from default-padding rows.
    /// 3 px covers both 1 px and 2 px chrome flips comfortably.</summary>
    private const double GridBoundaryDeadbandPx = 3.0;

    private void RecomputeGrid()
    {
        var (cols, rows) = _renderer.ComputeGrid(Bounds.Size);

        // Hysteresis around boundary-crossings. If the new tuple
        // differs from the current one by exactly one cell on either
        // axis AND Bounds is within GridBoundaryDeadbandPx of the
        // boundary that flipped it, snap back to the current value.
        // This protects against host-side layout jitter (e.g., a
        // focus-ring border-thickness flip swinging the inner area
        // by 2 px on every focus event) being misread as a real
        // resize. Larger Bounds changes — a dragged window edge,
        // splitter, font zoom — fall through unaffected.
        cols = ApplyBoundaryHysteresis(cols, _buffer.Cols, Bounds.Width,  _renderer.CellWidth);
        rows = ApplyBoundaryHysteresis(rows, _buffer.Rows, Bounds.Height, _renderer.CellHeight);

        // Either an unusable transient size or a return to the
        // current grid invalidates any earlier pending resize: that
        // stashed (cols, rows) was a momentary layout artefact, and
        // applying it 50ms later — when Bounds has settled back to
        // the current size — would emit a wrong Resized event and
        // pointlessly thrash the buffer. Drop both the pending
        // value and the timer.
        if (cols < MinUsableCols || rows < MinUsableRows
            || (cols == _buffer.Cols && rows == _buffer.Rows))
        {
            _pendingResize = null;
            _resizeDebounceTimer.Stop();
            return;
        }
        // Stash the target dims and (re)start the debounce timer.
        // ApplyPendingResize runs after activity stops; until then,
        // the buffer keeps its current dimensions and the PTY isn't
        // spammed with SIGWINCH-equivalent Resized events.
        _pendingResize = (cols, rows);
        _resizeDebounceTimer.Stop();
        _resizeDebounceTimer.Start();
    }

    private static int ApplyBoundaryHysteresis(int proposed, int current, double bounds, double cellSize)
    {
        if (proposed == current) return proposed;
        if (cellSize <= 0)        return proposed;
        // Single-cell flip only — multi-cell jumps are real resizes,
        // pass straight through.
        if (Math.Abs(proposed - current) != 1) return proposed;

        // The boundary that the proposed value sits just past.
        // Going from current 80 cols to proposed 79: boundary is
        // 80 * cellWidth (we crossed below it). Proposed 81: boundary
        // is 81 * cellWidth (we crossed above it). Pick whichever
        // applies via Math.Max.
        double boundary = Math.Max(proposed, current) * cellSize;
        return Math.Abs(bounds - boundary) < GridBoundaryDeadbandPx ? current : proposed;
    }

    private void ApplyPendingResize()
    {
        _resizeDebounceTimer.Stop();
        if (_pendingResize is not (int cols, int rows)) return;
        _pendingResize = null;
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

    /// <summary>Enable OpenType programming-font ligatures (Fira Code,
    /// JetBrains Mono, Cascadia Code, …). Substitutions like
    /// <c>==</c> → <c>⟹</c> only apply within a same-attribute glyph
    /// run; ligatures across SGR colour boundaries are intentionally
    /// not joined. Off by default — fonts without ligature features
    /// pay a small extra shaping cost for nothing.</summary>
    public bool EnableLigatures
    {
        get => _renderer.EnableLigatures;
        set
        {
            if (_renderer.EnableLigatures == value) return;
            _renderer.EnableLigatures = value;
            InvalidateVisual();
        }
    }

    // ---- Keyboard ----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // _altHeld feeds OnTextInput's "meta sends ESC" prefix.
        // AltGr (the right Alt key on most ISO keyboards) is reported
        // as Ctrl+Alt by Windows/Linux; on those layouts AltGr+Q is
        // how the user types `@`, AltGr+5 is `€`, etc. Treating that
        // as "Alt held" would prefix every AltGr-produced character
        // with ESC and ship `\e@` to the shell instead of `@`. Real
        // Alt-as-meta only fires with Alt and NOT Ctrl.
        _altHeld = (e.KeyModifiers & KeyModifiers.Alt)     != 0
                && (e.KeyModifiers & KeyModifiers.Control) == 0;

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
                case Key.A: _buffer.SelectAll();              e.Handled = true; return;
                case Key.K: _buffer.ClearScreenAndScrollback(); e.Handled = true; return;
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

        var bytes = KeyMapper.Map(e, _buffer.ApplicationCursorKeys, _buffer.ApplicationKeypad,
            _buffer.ModifyOtherKeys);
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
        _altHeld = (e.KeyModifiers & KeyModifiers.Alt)     != 0
                && (e.KeyModifiers & KeyModifiers.Control) == 0;
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
            e.Handled = true;
            return;
        }

        // OSC 8 hyperlink: single left-click on a linked cell opens the URL.
        if (_clickCount == 1 && btn == 0)
        {
            var cells = _buffer.GetRowForRender(row);
            if (cells != null && col < cells.Length && cells[col].HyperlinkId != 0
                && _buffer.TryGetHyperlink(cells[col].HyperlinkId, out var url))
            {
                ActivateLink(url);
                e.Handled = true;
                return;
            }
            // Plain-URL / custom link providers — most-recent wins. Run
            // only on click, not on every frame — keeps idle overhead at
            // zero. Convert the row to a string once per click attempt.
            // Snapshot the provider list once for a consistent iteration
            // — concurrent register/dispose can swap it under us.
            var providers = _linkProviders;
            if (cells != null && providers.Count > 0)
            {
                string rowText = RowText.Build(cells, out int[] colMap);
                for (int i = providers.Count - 1; i >= 0; i--)
                {
                    int seen = 0;
                    foreach (var link in providers[i].Provide(rowText))
                    {
                        // Same per-row cap the renderer enforces — keeps
                        // a runaway provider from spinning here on click.
                        if (++seen > 64) break;
                        // Translate string-index coords back to cell
                        // columns. Without the map, an astral rune
                        // earlier in the row would offset every URL
                        // hit-test by 1 column too far right.
                        int startCell = colMap[link.StartCol];
                        int endCell   = colMap[Math.Min(link.EndCol - 1, colMap.Length - 1)];
                        if (col >= startCell && col <= endCell)
                        {
                            ActivateLink(link.Url);
                            e.Handled = true;
                            return;
                        }
                    }
                }
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
        // Capture the pointer so we keep receiving OnPointerMoved
        // events once the user drags past the top or bottom edge of
        // the control. Without capture, Avalonia stops dispatching
        // pointer events the moment the cursor leaves the control's
        // bounds — which is exactly when the auto-scroll timer needs
        // to know "the pointer is still off-screen, keep scrolling".
        e.Pointer.Capture(this);
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
            {
                SendMouse(_pressedBtn + 32, row, col, e.KeyModifiers, pressed: true);
                e.Handled = true;
                return;
            }
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

                // Auto-scroll handoff: if the pointer is outside the
                // viewport vertically, the timer takes over and keeps
                // scrolling + extending while the button stays down,
                // even when the pointer doesn't move further. We track
                // _lastDragPos so the tick has the latest X coord for
                // the column endpoint.
                _lastDragPos = pos;
                bool outsideY = pos.Y < 0 || pos.Y >= Bounds.Height;
                if (outsideY)
                {
                    if (!_dragAutoScrollTimer.IsEnabled) _dragAutoScrollTimer.Start();
                }
                else if (_dragAutoScrollTimer.IsEnabled)
                {
                    _dragAutoScrollTimer.Stop();
                }
            }
        }
        else if (_buffer.MouseMode >= 1003 && _buffer.ScrollOffset == 0)
        {
            SendMouse(35, row, col, e.KeyModifiers, pressed: true); // btn=3 = motion without button
            e.Handled = true;
        }
    }

    /// <summary>Tick handler for the drag-select auto-scroll timer.
    /// Runs while the user holds the mouse button with the pointer
    /// outside the viewport vertically — scrolls one line per tick
    /// in the appropriate direction and extends the selection to
    /// the edge cell so the highlight grows with the scroll.</summary>
    private void DragAutoScrollTick()
    {
        // Defensive guards — if state changed between scheduling and
        // tick (button released, selection cleared, scrollback empty),
        // stop and bail.
        if (!_mouseDown || _disposed)
        {
            _dragAutoScrollTimer.Stop();
            return;
        }
        if (_buffer.MouseMode > 0 && _buffer.ScrollOffset == 0)
        {
            // App-mode mouse reporting owns drag — don't fight it.
            _dragAutoScrollTimer.Stop();
            return;
        }

        bool above = _lastDragPos.Y < 0;
        bool below = _lastDragPos.Y >= Bounds.Height;
        if (!above && !below)
        {
            _dragAutoScrollTimer.Stop();
            return;
        }

        if (above)
        {
            // Pointer is above the viewport — scroll up into
            // scrollback (if there is any). Stop when we hit the top.
            if (_buffer.ScrollOffset >= _buffer.ScrollbackCount)
            {
                _dragAutoScrollTimer.Stop();
                return;
            }
            _buffer.ScrollViewUp(1);
            ShowScrollbar();
        }
        else
        {
            // Pointer is below — scroll down toward the live screen.
            // Stop once we're back to offset zero.
            if (_buffer.ScrollOffset <= 0)
            {
                _dragAutoScrollTimer.Stop();
                return;
            }
            _buffer.ScrollViewDown(1);
            ShowScrollbar();
        }

        // Materialise the selection if the user pressed and dragged
        // straight off-edge without any in-bounds movement.
        if (_selectionPending)
        {
            _buffer.StartSelection(_pressedRow, _pressedCol);
            _selectionPending = false;
        }
        if (_buffer.Selection != null)
        {
            int edgeRow = above ? 0 : _buffer.Rows - 1;
            int col = Math.Clamp((int)(_lastDragPos.X / _renderer.CellWidth),
                                 0, _buffer.Cols - 1);
            _buffer.ExtendSelection(edgeRow, col);
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
        _dragAutoScrollTimer.Stop();
        // Release the capture grabbed in OnPointerPressed for drag-
        // selection. Without this the control would keep eating
        // pointer events the user expects to land on neighbouring
        // panes / windows.
        if (wasDown) e.Pointer.Capture(null);

        if (_buffer.MouseMode > 0 && _buffer.ScrollOffset == 0)
        {
            SendMouse(_pressedBtn, row, col, e.KeyModifiers, pressed: false);
            e.Handled = true;
            return;
        }

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
    // scale by ScrollSensitivity (roughly the height of 3 text lines,
    // the Windows default feel) so one notch advances about three rows.
    // The buffer does the fractional accumulation internally via
    // PixelScrollOffset — no integer rounding on our side.
    private const double DefaultScrollSensitivity = 40.0;

    /// <summary>Pixels of scroll per wheel notch / per unit of trackpad
    /// delta. Higher = faster scroll. Default ≈ 3 text lines per
    /// notch — the Windows default feel. Negative is clamped to 0.</summary>
    public double ScrollSensitivity { get; set; } = DefaultScrollSensitivity;

    /// <summary>Maximum lines of scrollback the primary screen retains.
    /// Lowering it discards older scrollback eagerly; raising it
    /// affects future evictions only. The alternate screen is always
    /// 0 (no scrollback by design).</summary>
    public int ScrollbackLimit
    {
        get => _buffer.ScrollbackLimit;
        set => _buffer.ScrollbackLimit = value;
    }

    /// <summary>Characters that count as word boundaries for
    /// double-click word selection. See
    /// <see cref="TerminalBuffer.WordSeparators"/> for default.</summary>
    public string WordSeparators
    {
        get => _buffer.WordSeparators;
        set => _buffer.WordSeparators = value;
    }

    /// <summary>Cursor blink period in milliseconds. Default 500. Set
    /// 0 to stop blinking entirely; the cursor stays solid regardless
    /// of DECSCUSR style.</summary>
    public int CursorBlinkIntervalMs
    {
        get => (int)_blinkTimer.Interval.TotalMilliseconds;
        set
        {
            if (value <= 0)
            {
                // Stop the timer AND force the visible state back on
                // — otherwise a setter call during the timer's hidden
                // phase leaves the cursor (and any SGR-blink cells)
                // permanently invisible until something else triggers
                // a Bump.
                _blinkTimer.Stop();
                _blinkVisible = true;
                _renderer.BlinkVisible = true;
                InvalidateVisual();
                return;
            }
            _blinkTimer.Interval = TimeSpan.FromMilliseconds(value);
            if (!_blinkTimer.IsEnabled) _blinkTimer.Start();
        }
    }

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
        _buffer.ScrollByPixels(e.Delta.Y * Math.Max(0, ScrollSensitivity), _renderer.CellHeight);
        ShowScrollbar();
        e.Handled = true;
    }

    private void SendMouse(int btn, int row, int col, KeyModifiers mods, bool pressed)
    {
        // X10 mouse mode (DECSET 9) is press-only: drop releases. Same
        // for motion (which we synthesise as btn+32 / btn=35) — X10 can't
        // encode them. Modern modes (1000/1002/1003) accept everything.
        if (_buffer.MouseMode == 9 && !pressed) return;
        if (_buffer.MouseMode == 9 && btn >= 32) return;

        int b = btn;
        if ((mods & KeyModifiers.Shift)   != 0) b += 4;
        if ((mods & KeyModifiers.Alt)     != 0) b += 8;
        if ((mods & KeyModifiers.Control) != 0) b += 16;

        byte[] seq;
        switch (_buffer.MouseEncoding)
        {
            case MouseEncoding.Sgr:
            {
                // ESC [ < b ; x ; y M  (press) / m (release). 1-based
                // coords; no column limit.
                char fin = pressed ? 'M' : 'm';
                seq = Encoding.ASCII.GetBytes($"\x1b[<{b};{col + 1};{row + 1}{fin}");
                break;
            }
            case MouseEncoding.SgrPixels:
            {
                // Same wire format, but x and y are pixel coordinates.
                // Compute from cell × cell-size; clamp to a sane lower
                // bound so apps that divide by zero on tiny grids
                // don't crash.
                int px = (int)Math.Max(1, (col + 0.5) * _renderer.CellWidth);
                int py = (int)Math.Max(1, (row + 0.5) * _renderer.CellHeight);
                char fin = pressed ? 'M' : 'm';
                seq = Encoding.ASCII.GetBytes($"\x1b[<{b};{px};{py}{fin}");
                break;
            }
            default:
            {
                // Legacy X10/1000 encoding: ESC [ M Cb Cx Cy with each
                // coordinate as a single byte at +32 offset. Releases
                // surface as button=3 (encoded as 35 = 32+3) since the
                // wire format has no press/release flag. Mouse columns
                // beyond 223 can't be encoded — clamp rather than
                // emit a corrupt sequence (matches xterm).
                int wireBtn = pressed ? b : 3 | (b & ~3); // release = button 3 in low 2 bits
                int cx = Math.Min(col + 1, 223);
                int cy = Math.Min(row + 1, 223);
                seq = new byte[] {
                    0x1B, (byte)'[', (byte)'M',
                    (byte)(wireBtn + 32),
                    (byte)(cx + 32),
                    (byte)(cy + 32),
                };
                break;
            }
        }
        // Route through RaiseInput so the local InputEventStream sees
        // mouse bytes alongside keystrokes — keeps observers consistent.
        // Origin = Programmatic since the bytes are control-plane
        // (mouse-event encoding), not user-typed text.
        RaiseInput(seq, InputLineOrigin.Programmatic);
    }

    private (int row, int col) GridPos(Point p)
    {
        // Smooth-scroll shifts the rendered grid DOWN by
        // PixelScrollOffset pixels (so an older row bleeds in at the
        // top during a wheel/trackpad scroll). The renderer draws
        // row r at `y = r*CellHeight + pixelShift`. To invert and
        // get the row at pixel y we have to subtract pixelShift
        // before dividing — without that, every click during a
        // mid-scroll lands one row too low (cell at pixel Y=20 with
        // pixelShift=10 is visually showing row 0 starting at Y=10
        // and ending at Y=10+CellHeight, but uncorrected math would
        // place Y=20 in row 1). Visible symptom: drag-selections
        // anchor on the cell *below* the click.
        //
        // Math.Floor (not int-cast truncation) so negative values
        // — which happen for clicks in the bleed region above the
        // viewport — round correctly before being clamped to 0.
        double yShifted = p.Y - _buffer.PixelScrollOffset;
        int row = Math.Clamp(
            (int)Math.Floor(yShifted / _renderer.CellHeight),
            0, _buffer.Rows - 1);
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

    /// <summary>Force-clear an OSC 8 hyperlink that got "stuck"
    /// because the close sequence (<c>OSC 8 ; ; ST</c>) was dropped
    /// upstream. After this, freshly typed cells stop inheriting the
    /// underline. Doesn't disturb SGR colours, cursor position, or
    /// screen contents.</summary>
    public void ClearActiveHyperlink() => _buffer.ClearActiveHyperlink();

    /// <summary>DECSTR equivalent. Clears SGR pen, cursor visibility,
    /// scroll region, insert/origin modes, charset slots. Screen
    /// content and scrollback are preserved. Use as a "reset
    /// formatting" recovery path.</summary>
    public void SoftReset() => _buffer.SoftResetTerminal();

    /// <summary>RIS equivalent. Clears both screens, scrollback, all
    /// DEC modes, SGR pen, cursor state, OSC 8 / title / palette
    /// state. The nuclear option for "my terminal is broken, start
    /// over".</summary>
    public void Reset() => _buffer.ResetTerminal();

    /// <summary>Wipe the live screen contents and scrollback ring,
    /// move the cursor to (0,0), and snap the viewport to the live
    /// bottom. SGR pen, DEC modes, OSC 8 / palette / title state are
    /// preserved (use <see cref="Reset"/> for the heavier RIS).
    /// Wired to Cmd+K / Ctrl+Shift+K — iTerm2's "Clear Buffer"
    /// semantics. Note: the shell on the far side of the PTY won't
    /// know the screen got cleared, so the prompt only redraws on
    /// the next interaction.</summary>
    public void ClearScreenAndScrollback() => _buffer.ClearScreenAndScrollback();

    /// <summary>Call before connecting a freshly-spawned PTY whose
    /// startup output you want to land on a clean canvas. Equivalent
    /// to <see cref="Reset"/>, but named to make the intent obvious
    /// at the integration site. Useful for apps that don't use
    /// alt-screen for their welcome banner (Claude Code, some
    /// REPLs) where dimension-detection races during startup
    /// otherwise leave stacked partial renders in scrollback.
    /// <example>
    /// <code>
    /// terminal.PrepareForNewSession();
    /// pty.StdoutBytes.Subscribe(b =&gt; terminal.Write(b));
    /// </code>
    /// </example>
    /// </summary>
    public void PrepareForNewSession() => _buffer.ResetTerminal();

    /// <summary>True when there is a non-empty selection in the buffer.</summary>
    public bool HasSelection => _buffer.Selection != null;

    /// <summary>Plain text of the current selection, or empty when
    /// nothing is selected. Wide-cell continuations are skipped so the
    /// returned text matches what the renderer drew.</summary>
    public string GetSelectionText() => _buffer.GetSelectedText();

    /// <summary>(startRow, startCol, endRow, endCol) of the current
    /// selection in absolute-row coordinates (0 = oldest scrollback),
    /// or null when there's no selection. Useful for hosts that want
    /// to drive a "highlighted range" badge.</summary>
    public (int StartRow, int StartCol, int EndRow, int EndCol)? GetSelectionPosition()
    {
        var s = _buffer.Selection;
        if (s == null) return null;
        var (r1, c1, r2, c2) = s.Normalized();
        return (r1, c1, r2, c2);
    }

    /// <summary>Programmatically select a single absolute-row range
    /// from (startRow, startCol) to (endRow, endCol), inclusive.
    /// Coordinates are absolute (0 = oldest scrollback row); a
    /// caller-friendly mapping the host can derive from
    /// <see cref="TerminalBuffer.VisualToAbsRow"/>.</summary>
    public void Select(int startRow, int startCol, int endRow, int endCol)
        => _buffer.Select(startRow, startCol, endRow, endCol);

    /// <summary>Select a whole absolute row.</summary>
    public void SelectLineByAbs(int absRow)
        => _buffer.Select(absRow, 0, absRow, _buffer.Cols - 1);

    /// <summary>Drop any active selection.</summary>
    public void ClearSelection() => _buffer.ClearSelection();

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
    /// calls cancel the previous scan. <paramref name="options"/>
    /// controls case-sensitivity, whole-word and regex modes — the
    /// default reproduces the legacy case-insensitive plain-text
    /// search.</summary>
    public void Find(string? needle, SearchOptions? options = null)
    {
        // Cancel the previous scan, but DON'T dispose the CTS here.
        // The previous RunFindAsync task captured the CTS's token;
        // disposing the CTS while the task is mid-await on the token
        // races between OperationCanceledException (clean) and
        // ObjectDisposedException (caught by the generic catch and
        // logged as an error). Hand ownership to the task — it
        // disposes in its finally.
        _searchCts?.Cancel();
        _searchCts = null;

        if (string.IsNullOrEmpty(needle))
        {
            _buffer.ClearSearch();
            return;
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        int gen = ++_searchGeneration;
        _ = RunFindAsync(needle, options ?? SearchOptions.Default, gen, cts);
    }

    private async Task RunFindAsync(string needle, SearchOptions options, int gen, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            await Task.Delay(SearchDebounceMs, ct).ConfigureAwait(true);
            if (gen != _searchGeneration) return;

            // Snapshot has to run on the UI thread — it reads the
            // Scrollback ring and live-screen rows which are mutated
            // by PTY writes on the same thread.
            var snapshot = _buffer.SnapshotRows();

            var matches = await Task.Run(
                () => TerminalBuffer.ScanMatches(snapshot, needle, options, ct),
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
        finally
        {
            cts.Dispose();
        }
    }

    /// <summary>Jump to the next search match.</summary>
    public void FindNext() => _buffer.NextMatch();

    /// <summary>Jump to the previous search match.</summary>
    public void FindPrev() => _buffer.PrevMatch();

    /// <summary>Leave find mode — drops matches, hides highlights, and
    /// invalidates any in-flight async search so its results can't
    /// reappear after the find UI closes. Equivalent to calling
    /// <c>Find(null)</c>.</summary>
    public void CloseFind() => Find(null);

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

    private async Task PasteFromClipboardAsyncCore()
    {
        var cb = TopLevel.GetTopLevel(this)?.Clipboard;
        if (cb == null) return;

        using var transfer = await cb.TryGetDataAsync();
        if (transfer == null) return;

        // Order matters here: image-bearing clipboards on every
        // platform we care about ALSO surface a synthetic-text
        // representation (UTF-8-decoded raw bytes, base64, image
        // metadata, etc. — varies by source app) which Paste would
        // otherwise forward to the shell as garbage. Try each
        // structured form before falling through to text.

        // 1. File reference (Finder copy, drag source, "Copy as path"
        // shell extensions). Avalonia normalises cross-platform file
        // formats into DataFormat.File.
        var file = await transfer.TryGetFileAsync();
        if (file != null)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path)) { Paste(path); return; }
        }

        // 2. Image bytes (screenshot-to-clipboard, "Copy Image"
        // from a browser / image viewer, native paint apps).
        // TryGetBitmapAsync is Avalonia's cross-platform image
        // extractor; on a good day it handles macOS
        // public.png/public.tiff, Windows CF_DIB/CF_DIBV5/PNG,
        // X11 image/png. Spill to a temp PNG and paste the path
        // so CLIs that accept image-file arguments can pick it up.
        try
        {
            var bitmap = await transfer.TryGetBitmapAsync();
            if (bitmap != null)
            {
                var path = WriteClipboardBitmapToTemp(bitmap);
                Paste(path);
                return;
            }
        }
        catch (Exception ex)
        {
            TerminalLog.Error($"[TerminalControl] clipboard bitmap decode failed: {ex.Message}");
            // fall through to raw-bytes fallback, then text
        }

        // 2b. Raw-bytes fallback. Avalonia's TryGetBitmapAsync on
        // Windows misses real-world clipboards in practice — Snipping
        // Tool, modern browsers and paint apps put PNG bytes under
        // the "PNG" CF and CF_DIB / CF_DIBV5, but Avalonia 11.3 only
        // surfaces those as DataFormat.Bitmap when the source app
        // also wrote one of the formats Avalonia's decoder happens
        // to recognise. When it doesn't, the typed extractor returns
        // null and the user sees a silent paste failure. So: walk
        // the items × formats matrix, pull bytes for any identifier
        // that smells like an image, sniff magic bytes to confirm,
        // and write to a temp file. Magic-byte sniffing beats
        // identifier matching when the platform exposes a format
        // string we don't recognise.
        try
        {
            var imagePath = await TryWriteClipboardImageBytesAsync(transfer);
            if (imagePath != null) { Paste(imagePath); return; }
        }
        catch (Exception ex)
        {
            TerminalLog.Error($"[TerminalControl] clipboard image-bytes fallback failed: {ex.Message}");
        }

        // 3. Plain text — the common case. This path only fires
        // when neither file refs nor a decodable bitmap surfaced;
        // for image-bearing clipboards the bitmap path above wins
        // and we never get here. (That ordering matters: pasting
        // the UTF-8-decoded bytes of a PNG produces literal
        // garbage / NULs / spaces, and Paste's NUL-refuser would
        // discard it anyway — leaving the user with an unhelpful
        // silent paste failure.)
        var t = await transfer.TryGetTextAsync();
        if (!string.IsNullOrEmpty(t)) Paste(t);
    }

    // Identifiers worth pulling raw bytes for. Permissive — the magic-
    // byte sniffer downstream is what actually decides whether to keep
    // the payload. Strings cover Win32 clipboard format names,
    // mime types (X11/Wayland and some Windows apps), and macOS UTIs.
    private static readonly string[] s_clipboardImageIdentifiers =
    {
        "PNG",  "image/png",  "public.png",
        "JFIF", "image/jpeg", "public.jpeg", "image/jpg",
        "TIFF", "image/tiff", "public.tiff",
        "image/bmp", "BMP",
        // CF_DIB / CF_DIBV5 surface under several string names depending
        // on the Avalonia backend version. We accept all of them and
        // wrap a synthetic BMP file header on the way out.
        "DeviceIndependentBitmap", "CF_DIB", "CF_DIBV5", "Format8", "Format17",
    };

    private static async Task<string?> TryWriteClipboardImageBytesAsync(IAsyncDataTransfer transfer)
    {
        List<string>? seen = null;
        foreach (var item in transfer.Items)
        {
            foreach (var format in item.Formats)
            {
                (seen ??= new List<string>()).Add(format.Identifier);
                if (Array.IndexOf(s_clipboardImageIdentifiers, format.Identifier) < 0) continue;

                object? raw;
                try { raw = await item.TryGetRawAsync(format); }
                catch (Exception ex)
                {
                    TerminalLog.Error($"[TerminalControl] clipboard raw read failed for {format.Identifier}: {ex.Message}");
                    continue;
                }
                if (raw is not byte[] bytes || bytes.Length == 0) continue;

                var (ext, payload) = NormaliseClipboardImageBytes(bytes);
                if (ext == null) continue;
                return WriteClipboardBytesToTemp(payload, ext);
            }
        }

        if (seen != null && seen.Count > 0)
            TerminalLog.Trace($"[TerminalControl] clipboard had no decodable image bytes; formats present: {string.Join(", ", seen)}");
        return null;
    }

    private static (string? Extension, byte[] Bytes) NormaliseClipboardImageBytes(byte[] bytes)
    {
        // Trust the bytes over the format identifier. Sources lie —
        // an app that registers PNG bytes under a Win32 format named
        // "Bitmap" is not unheard of.
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G')
            return (".png", bytes);
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return (".jpg", bytes);
        if (bytes.Length >= 4 &&
            ((bytes[0] == (byte)'I' && bytes[1] == (byte)'I' && bytes[2] == 0x2A && bytes[3] == 0x00) ||
             (bytes[0] == (byte)'M' && bytes[1] == (byte)'M' && bytes[2] == 0x00 && bytes[3] == 0x2A)))
            return (".tiff", bytes);
        if (bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
            return (".bmp", bytes);

        // CF_DIB / CF_DIBV5 — payload is a DIB (BITMAPINFOHEADER /
        // BITMAPV4HEADER / BITMAPV5HEADER + colour table + pixels)
        // WITHOUT the 14-byte BITMAPFILEHEADER. Prepend the header
        // so the file we write is a valid .bmp readable by anything
        // that ingests image paths.
        if (bytes.Length >= 4)
        {
            int dibHeaderSize = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);
            if (dibHeaderSize is 40 or 52 or 56 or 108 or 124)
                return (".bmp", AddBmpFileHeader(bytes, dibHeaderSize));
        }

        return (null, bytes);
    }

    private static byte[] AddBmpFileHeader(byte[] dib, int dibHeaderSize)
    {
        // 14-byte BITMAPFILEHEADER: "BM" + file size + 4 bytes
        // reserved + offset-to-pixels.
        // Pixel offset = 14 + DIB header size + colour-table size.
        // Colour table is biClrUsed * 4 bytes for paletted images
        // (1/4/8 bpp); 0 otherwise. biClrUsed lives at offset 32
        // in BITMAPINFOHEADER and equivalents (V4/V5 share layout).
        int clrUsed = 0;
        if (dib.Length >= 36)
            clrUsed = dib[32] | (dib[33] << 8) | (dib[34] << 16) | (dib[35] << 24);
        int bpp = 0;
        if (dib.Length >= 16)
            bpp = dib[14] | (dib[15] << 8);
        if (clrUsed == 0 && bpp is 1 or 4 or 8)
            clrUsed = 1 << bpp;
        int pixelOffset = 14 + dibHeaderSize + clrUsed * 4;
        int fileSize = 14 + dib.Length;

        var output = new byte[fileSize];
        output[0] = (byte)'B'; output[1] = (byte)'M';
        output[2] = (byte)(fileSize);
        output[3] = (byte)(fileSize >> 8);
        output[4] = (byte)(fileSize >> 16);
        output[5] = (byte)(fileSize >> 24);
        // 6..9 reserved (zero by default)
        output[10] = (byte)(pixelOffset);
        output[11] = (byte)(pixelOffset >> 8);
        output[12] = (byte)(pixelOffset >> 16);
        output[13] = (byte)(pixelOffset >> 24);
        System.Buffer.BlockCopy(dib, 0, output, 14, dib.Length);
        return output;
    }

    private static string WriteClipboardBytesToTemp(byte[] bytes, string extension)
    {
        var dir = Path.Combine(Path.GetTempPath(), PasteImageDirectoryName);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"paste-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}{extension}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Directory name under the OS temp dir where pasted
    /// images are spilled. Hosts can override to segregate or brand
    /// the path (e.g. an IDE might use its own prefix).</summary>
    public static string PasteImageDirectoryName { get; set; } = "exclr8-terminal-paste";

    /// <summary>Save a clipboard <see cref="Avalonia.Media.Imaging.Bitmap"/>
    /// to a temp PNG and return the path. Avalonia's
    /// <c>Bitmap.Save(string)</c> picks the encoder by file
    /// extension; PNG round-trips losslessly through any source
    /// format and is the safest default for "this image was just
    /// in the clipboard". The synchronous Save is fine — on the
    /// rare occasions it's slow, that's the encoder running on
    /// the UI thread for ~milliseconds; the alternative would be
    /// queuing a Task and pasting an as-yet-unwritten path.</summary>
    private static string WriteClipboardBitmapToTemp(Avalonia.Media.Imaging.Bitmap bitmap)
    {
        var dir = Path.Combine(Path.GetTempPath(), PasteImageDirectoryName);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"paste-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.png");
        bitmap.Save(path);
        return path;
    }

    // ---- Focus ----
    //
    // Two distinct concerns share the word "focus" here:
    //
    //   1. Local visual state — cursor outline (focused vs. just-an-
    //      empty-rect), cursor blink reset. Tied to the *control's*
    //      Avalonia focus, because that mirrors "where is keyboard
    //      input going right now". Always handled in OnGotFocus /
    //      OnLostFocus.
    //
    //   2. The DECSET 1004 PTY focus-event protocol — `\e[I` / `\e[O`
    //      sent to the shell so apps like vim, claude, codex, tmux
    //      can react to "the user left and came back". Tied to the
    //      *top-level window* activation, NOT to control focus,
    //      because in a host that hosts multiple terminal panes /
    //      tabs the user switches between them constantly without
    //      ever leaving the terminal session. Wiring focus events
    //      to control focus made every tab switch fire \e[O \e[I,
    //      and TUIs that aren't perfectly idempotent on focus-back
    //      (Codex's prompt walking down per redraw) accumulate
    //      visible drift. iTerm2 / Terminal.app / WezTerm all use
    //      window-level focus for the same reason.
    //
    // Hosts that legitimately need pane-level focus tracking can
    // opt back into control-level firing via FocusEventSource =
    // FocusEventSource.Control.

    /// <summary>Where DECSET 1004 focus events come from. Default
    /// <see cref="FocusEventSource.TopLevel"/>: <c>\e[I</c> /
    /// <c>\e[O</c> only fire when the OS window gains/loses
    /// activation, so the user can switch terminal tabs without
    /// notifying every shell that they "left". Set to
    /// <see cref="FocusEventSource.Control"/> for the legacy
    /// per-control behaviour.</summary>
    public FocusEventSource FocusEventSource { get; set; } = FocusEventSource.TopLevel;

    private TopLevel? _focusTopLevel;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _focusTopLevel = TopLevel.GetTopLevel(this);
        if (_focusTopLevel is Window w)
        {
            w.Activated   += OnTopLevelActivated;
            w.Deactivated += OnTopLevelDeactivated;
        }
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_focusTopLevel is Window w)
        {
            w.Activated   -= OnTopLevelActivated;
            w.Deactivated -= OnTopLevelDeactivated;
        }
        _focusTopLevel = null;
    }

    private void OnTopLevelActivated(object? sender, EventArgs e)
    {
        if (FocusEventSource != FocusEventSource.TopLevel) return;
        _buffer.NotifyFocus(true);
        DrainBufferReplies();
    }

    private void OnTopLevelDeactivated(object? sender, EventArgs e)
    {
        if (FocusEventSource != FocusEventSource.TopLevel) return;
        _buffer.NotifyFocus(false);
        DrainBufferReplies();
    }

    private void DrainBufferReplies()
    {
        var replies = _buffer.TakeReplies();
        if (replies != null) Output?.Invoke(this, replies);
    }

    // Avalonia 12 unified focus event args: both overrides now take
    // FocusChangedEventArgs (was GotFocusEventArgs / RoutedEventArgs in 11).
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        // PTY focus event only when the host opted into the legacy
        // per-control source. Local visual state always updates so
        // cursor outline + blink reflect the keyboard-focus position.
        if (FocusEventSource == FocusEventSource.Control)
        {
            _buffer.NotifyFocus(true);
            DrainBufferReplies();
        }
        _blinkVisible = true;
        _renderer.BlinkVisible = true;
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        if (FocusEventSource == FocusEventSource.Control)
        {
            _buffer.NotifyFocus(false);
            DrainBufferReplies();
        }
        InvalidateVisual();
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
        _syncOutputTimer.Stop();
        _resizeDebounceTimer.Stop();
        _dragAutoScrollTimer.Stop();

        // The buffer is exposed publicly and a host may keep a
        // reference to it after disposing the control (e.g. for
        // post-mortem inspection). Detach our handlers so the buffer
        // doesn't hold this control alive through them.
        _buffer.Changed                   -= OnBufferChanged;
        _buffer.SynchronizedOutputChanged -= OnSynchronizedOutputChanged;
        _buffer.PaletteChanged            -= OnPaletteChanged;

        // Cancel only — RunFindAsync's finally disposes. Disposing
        // here too would race the in-flight task and could throw
        // ObjectDisposedException on its next await.
        _searchCts?.Cancel();
        _searchCts = null;

        IProcessChildWatcher? toDispose;
        lock (_processWatchLock) toDispose = StopWatcher_Locked();
        // Dispose outside the lock to avoid the same deadlock the
        // remove handler guards against.
        if (toDispose != null) try { toDispose.Dispose(); } catch { }

        GC.SuppressFinalize(this);
    }
}
