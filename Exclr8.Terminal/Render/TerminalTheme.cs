using Avalonia.Media;

namespace Exclr8.Terminal.Render;

/// <summary>
/// Optional color overrides. Null fields fall back to
/// <see cref="TerminalPalette"/> defaults. Passed to
/// <c>TerminalControl.ColorScheme</c> to re-skin without subclassing.
/// </summary>
public sealed class TerminalTheme
{
    public Color? Foreground { get; init; }
    public Color? Background { get; init; }
    public Color? Cursor     { get; init; }

    /// <summary>16-entry ANSI palette override. Null slots use
    /// <see cref="TerminalPalette"/>'s built-in values.</summary>
    public Color?[]? AnsiColors { get; init; }
}
