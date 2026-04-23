using System;
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
/// Native Avalonia terminal renderer. Replaces the CEF + xterm.js stack
/// VibeCoder used to ship with. Feeds raw PTY bytes via
/// <see cref="Write(byte[])"/>, emits <see cref="Input"/> (bytes the
/// user typed, ready for the PTY writer) and <see cref="Resized"/>
/// (cell-grid dimensions to forward to <c>pty.Resize</c>).
/// </summary>
public class TerminalControl : Control
{
    private readonly TerminalRenderer _renderer;
    private readonly TerminalBuffer _buffer;
    private int _lastRevision = -1;

    /// <summary>Emitted when the user types — payload is already the
    /// byte sequence xterm would have sent to the PTY.</summary>
    public event EventHandler<ReadOnlyMemory<byte>>? Input;

    /// <summary>Emitted whenever the cell-grid size changes because
    /// the control was resized. Consumers forward to
    /// <c>pty.Resize(Cols, Rows)</c>.</summary>
    public event EventHandler<(int Cols, int Rows)>? Resized;

    public TerminalControl()
    {
        Focusable = true;
        ClipToBounds = true;

        _renderer = new TerminalRenderer();
        _buffer = new TerminalBuffer(80, 24);

        this.GetObservable(BoundsProperty).Subscribe(new AnonymousObserver<Rect>(_ => RecomputeGrid()));
    }

    public TerminalBuffer Buffer => _buffer;

    /// <summary>Feed raw PTY bytes into the terminal.</summary>
    public void Write(ReadOnlySpan<byte> bytes)
    {
        _buffer.Write(bytes);
        if (_buffer.Revision != _lastRevision) InvalidateVisual();
    }

    public void Write(byte[] bytes) => Write(bytes.AsSpan());

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        _renderer.Render(ctx, _buffer, Bounds.Size, IsFocused);
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        _altHeld = (e.KeyModifiers & KeyModifiers.Alt) != 0;
        var bytes = KeyMapper.Map(e);
        if (bytes.Length > 0)
        {
            Input?.Invoke(this, bytes);
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        _altHeld = (e.KeyModifiers & KeyModifiers.Alt) != 0;
    }

    // Track Alt state from OnKeyDown so TextInput can send ESC-prefix
    // for Alt+char (xterm's "meta sends ESC" convention).
    private bool _altHeld;

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (string.IsNullOrEmpty(e.Text)) return;
        var bytes = KeyMapper.MapTextInput(e.Text, _altHeld);
        if (bytes.Length > 0)
        {
            Input?.Invoke(this, bytes);
            e.Handled = true;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    // Avalonia's IObservable<T>.Subscribe(Action<T>) extension lives in
    // System.Reactive, which we don't want to pull in. Tiny adapter:
    private sealed class AnonymousObserver<T> : IObserver<T>
    {
        private readonly Action<T> _onNext;
        public AnonymousObserver(Action<T> onNext) { _onNext = onNext; }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) { _onNext(value); }
    }
}
