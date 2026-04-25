using System;

namespace Exclr8.Terminal.Buffer;

/// <summary>
/// Persistent reference to a line of buffer content. Survives scroll-
/// into-scrollback so hosts can attach decorations and "jump to
/// previous prompt" navigation without polling cell state.
///
/// <para>The marker stores a monotonically-increasing global row
/// index; <see cref="Line"/> resolves to the current absolute row by
/// subtracting the buffer's eviction count. When the anchored line
/// scrolls off the top of scrollback (i.e. eviction has caught up),
/// <see cref="IsValid"/> goes false and <see cref="Line"/> returns
/// -1. Disposing detaches the marker from the buffer's tracking list.
/// </para>
/// </summary>
public sealed class TerminalMarker : IDisposable
{
    private readonly TerminalBuffer _buf;
    private readonly long _globalAbs;
    private bool _disposed;

    public bool IsDisposed => _disposed;

    /// <summary>Fired exactly once when the marker is disposed (either
    /// by an explicit Dispose call or because its line scrolled off
    /// the top of scrollback). Hosts attach to this to clean up
    /// related state (e.g. a decoration anchored to it).</summary>
    public event EventHandler? Disposed;

    /// <summary>Current absolute row of the marker's anchored line
    /// (0 = oldest scrollback line at the moment of the call), or -1
    /// if the line has scrolled off / the marker is disposed.</summary>
    public int Line
    {
        get
        {
            if (_disposed) return -1;
            long current = _globalAbs - _buf.ScrollbackEvictions;
            return current >= 0 ? (int)current : -1;
        }
    }

    public bool IsValid => !_disposed && Line >= 0;

    internal TerminalMarker(TerminalBuffer buf, int currentAbs)
    {
        _buf = buf;
        _globalAbs = currentAbs + buf.ScrollbackEvictions;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disposed?.Invoke(this, EventArgs.Empty);
        _buf.RemoveMarker(this);
    }
}

/// <summary>Where a decoration sits in the render order. Bottom layer
/// paints behind cell content; top layer paints over it.</summary>
public enum DecorationLayer { Bottom, Top }

/// <summary>Construction parameters for <see cref="TerminalDecoration"/>.</summary>
public sealed class DecorationOptions
{
    public TerminalMarker Marker { get; init; } = null!;
    /// <summary>Starting column (0-based, inclusive). Negative values
    /// are clamped to 0 by the renderer.</summary>
    public int X { get; init; }
    /// <summary>Width in columns. 0 means "the whole row from
    /// <see cref="X"/>"; negative values are ignored.</summary>
    public int Width { get; init; }
    /// <summary>Optional background colour as packed 0xRRGGBB. Null
    /// means no fill — useful when the host only wants to draw a
    /// foreground glyph through a custom layer.</summary>
    public uint? BackgroundRgb { get; init; }
    public uint? ForegroundRgb { get; init; }
    public DecorationLayer Layer { get; init; } = DecorationLayer.Bottom;
}

/// <summary>Visual overlay anchored to a <see cref="TerminalMarker"/>.
/// The renderer walks the buffer's decoration list every frame and
/// paints those whose marker line is in the visible viewport.</summary>
public sealed class TerminalDecoration : IDisposable
{
    private readonly TerminalBuffer _buf;
    private bool _disposed;

    public TerminalMarker Marker { get; }
    public int X { get; }
    public int Width { get; }
    public uint? BackgroundRgb { get; }
    public uint? ForegroundRgb { get; }
    public DecorationLayer Layer { get; }

    public bool IsDisposed => _disposed;
    public event EventHandler? Disposed;

    internal TerminalDecoration(TerminalBuffer buf, DecorationOptions opts)
    {
        _buf = buf;
        Marker = opts.Marker;
        X = opts.X;
        Width = opts.Width;
        BackgroundRgb = opts.BackgroundRgb;
        ForegroundRgb = opts.ForegroundRgb;
        Layer = opts.Layer;
        // When the marker disposes, the decoration is meaningless —
        // tear it down so the renderer doesn't keep dereferencing a
        // ghost marker.
        Marker.Disposed += OnMarkerDisposed;
    }

    private void OnMarkerDisposed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Marker.Disposed -= OnMarkerDisposed;
        Disposed?.Invoke(this, EventArgs.Empty);
        _buf.RemoveDecoration(this);
    }
}
