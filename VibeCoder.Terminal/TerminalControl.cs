using System;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using VibeCoder.Terminal.Buffer;
using VibeCoder.Terminal.Input;
using VibeCoder.Terminal.Render;

namespace VibeCoder.Terminal;

/// <summary>
/// Native Avalonia terminal renderer. Consumers feed raw PTY bytes via
/// <see cref="Write(byte[])"/>, handle <see cref="Input"/> (bytes the
/// user typed), <see cref="Output"/> (DSR/DA replies the terminal
/// requested we forward to the PTY), <see cref="Resized"/> (new cell
/// grid), and optionally <see cref="HyperlinkClicked"/>.
/// </summary>
public class TerminalControl : Control
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

    // Scrollbar drag state. When the user pointer-presses on the right-
    // edge strip we enter scrollbar-drag mode; subsequent PointerMoved
    // events update ScrollOffset until PointerReleased.
    private bool _scrollbarDrag;

    // Cursor blink timer — toggles the renderer's BlinkVisible flag.
    private readonly DispatcherTimer _blinkTimer;
    private bool _blinkVisible = true;

    private bool _altHeld;
    private TerminalTheme? _theme;

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

    /// <summary>Optional color overrides. Null = defaults.</summary>
    public TerminalTheme? Theme
    {
        get => _theme;
        set { _theme = value; InvalidateVisual(); }
    }

    public TerminalControl()
    {
        Focusable    = true;
        ClipToBounds = true;

        _renderer = new TerminalRenderer();
        _buffer   = new TerminalBuffer(80, 24);

        this.GetObservable(BoundsProperty)
            .Subscribe(new ActionObserver<Rect>(_ => RecomputeGrid()));

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _blinkTimer.Tick += (_, _) =>
        {
            _blinkVisible           = !_blinkVisible;
            _renderer.BlinkVisible  = _blinkVisible;
            var s = _buffer.CursorStyle;
            if (_buffer.CursorVisible &&
                s is CursorStyle.BlockBlink or CursorStyle.UnderlineBlink or CursorStyle.BarBlink)
                InvalidateVisual();
        };
        _blinkTimer.Start();
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

    /// <summary>
    /// Paste text into the terminal. Wraps in <c>ESC[200~</c> /
    /// <c>ESC[201~</c> when DECSET 2004 (bracketed paste) is active —
    /// lets the shell distinguish typed vs pasted input.
    /// </summary>
    public void Paste(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        byte[] payload;
        if (_buffer.BracketedPaste)
        {
            var inner = Encoding.UTF8.GetBytes(text);
            payload = new byte[inner.Length + 12];
            "\x1b[200~"u8.CopyTo(payload);
            inner.CopyTo(payload, 6);
            "\x1b[201~"u8.CopyTo(payload.AsSpan(6 + inner.Length));
        }
        else
        {
            payload = Encoding.UTF8.GetBytes(text);
        }
        Input?.Invoke(this, payload);
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        _renderer.Render(ctx, _buffer, Bounds.Size, IsFocused, _theme);
        _lastRevision = _buffer.Revision;
    }

    private void RecomputeGrid()
    {
        var (cols, rows) = _renderer.ComputeGrid(Bounds.Size);
        if (cols <= 0 || rows <= 0) return;
        if (cols == _buffer.Cols && rows == _buffer.Rows) return;
        _buffer.Resize(cols, rows);
        Resized?.Invoke(this, (cols, rows));
        InvalidateVisual();
    }

    // ---- Keyboard ----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        _altHeld = (e.KeyModifiers & KeyModifiers.Alt) != 0;
        _buffer.ResetScrollOffset();

        bool isMac = OperatingSystem.IsMacOS();
        bool meta  = (e.KeyModifiers & KeyModifiers.Meta)    != 0;
        bool ctrl  = (e.KeyModifiers & KeyModifiers.Control) != 0;
        bool shift = (e.KeyModifiers & KeyModifiers.Shift)   != 0;

        // Clipboard shortcuts. ⌘C / ⌘V on macOS, Ctrl+Shift+C/V elsewhere —
        // Ctrl+C alone is SIGINT and must reach the shell.
        if ((isMac && meta && e.Key == Key.V) || (!isMac && ctrl && shift && e.Key == Key.V))
        {
            _ = PasteFromClipboardAsync();
            e.Handled = true;
            return;
        }
        if ((isMac && meta && e.Key == Key.C) || (!isMac && ctrl && shift && e.Key == Key.C))
        {
            _ = CopySelectionAsync();
            e.Handled = true;
            return;
        }

        var bytes = KeyMapper.Map(e, _buffer.ApplicationCursorKeys, _buffer.ApplicationKeypad);
        if (bytes.Length > 0) { Input?.Invoke(this, bytes); e.Handled = true; }
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
        _buffer.ResetScrollOffset();
        var bytes = KeyMapper.MapTextInput(e.Text, _altHeld);
        if (bytes.Length > 0) { Input?.Invoke(this, bytes); e.Handled = true; }
    }

    // ---- Mouse ----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        var pos = e.GetPosition(this);

        // Scrollbar drag: left-click on the right-edge strip starts
        // a scrollbar drag. Take priority over selection.
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsLeftButtonPressed
            && _buffer.ScrollbackCount > 0
            && pos.X >= Bounds.Width - TerminalRenderer.ScrollbarWidth)
        {
            _scrollbarDrag = true;
            _buffer.SetScrollOffset(
                TerminalRenderer.YToScrollOffset(pos.Y, _buffer.ScrollbackCount, _buffer.Rows, Bounds.Height));
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

        if      (_clickCount >= 3) _buffer.SelectLine(row);
        else if (_clickCount == 2) _buffer.SelectWord(row, col);
        else                       _buffer.StartSelection(row, col);
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
            e.Handled = true;
            return;
        }

        var (row, col) = GridPos(pos);

        if (_mouseDown)
        {
            if (_buffer.MouseMode >= 1002 && _buffer.ScrollOffset == 0)
            { SendMouse(_pressedBtn + 32, row, col, e.KeyModifiers, pressed: true); return; }
            if (_buffer.MouseMode == 0 || _buffer.ScrollOffset > 0)
                _buffer.ExtendSelection(row, col);
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

        if (wasDown && _buffer.Selection != null)
        {
            _buffer.ExtendSelection(row, col);
            var text = _buffer.GetSelectedText();
            if (!string.IsNullOrEmpty(text)) _ = CopyToClipboardAsync(text);
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
        // units. Buffer clamps at scrollback bounds.
        _buffer.ScrollByPixels(e.Delta.Y * PixelsPerTick, _renderer.CellHeight);
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

    private async Task CopySelectionAsync()
    {
        var t = _buffer.GetSelectedText();
        if (!string.IsNullOrEmpty(t)) await CopyToClipboardAsync(t);
    }

    private async Task CopyToClipboardAsync(string text)
    {
        try { await (TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask); }
        catch { }
    }

    private async Task PasteFromClipboardAsync()
    {
        try
        {
            var cb = TopLevel.GetTopLevel(this)?.Clipboard;
            if (cb == null) return;
            var t = await cb.GetTextAsync();
            if (!string.IsNullOrEmpty(t)) Paste(t);
        }
        catch { }
    }

    // ---- Focus ----

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        if (_buffer.FocusEvents) Input?.Invoke(this, "\x1b[I"u8.ToArray());
        _blinkVisible = true;
        _renderer.BlinkVisible = true;
        InvalidateVisual();
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        if (_buffer.FocusEvents) Input?.Invoke(this, "\x1b[O"u8.ToArray());
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
}
