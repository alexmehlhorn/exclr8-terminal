namespace Exclr8.Terminal;

/// <summary>Where the terminal control derives its DECSET 1004 focus
/// events from. The PTY-side <c>\e[I</c> / <c>\e[O</c> notification
/// can be wired to either the OS window's activation state (what
/// real terminals do) or the Avalonia control's keyboard-focus
/// state (the legacy default before this enum existed).</summary>
public enum FocusEventSource
{
    /// <summary>OS window activation. Tab / pane switches inside the
    /// host app are silent — only switching to a different OS
    /// window fires <c>\e[I</c> / <c>\e[O</c>. Default. Matches
    /// iTerm2 / Terminal.app / WezTerm. Prevents TUIs that redraw
    /// on focus events (Codex, Claude Code, vim with autoread)
    /// from accumulating per-tab-switch drift.</summary>
    TopLevel,

    /// <summary>Avalonia control keyboard focus. Every focus shift
    /// inside the host UI — including tab switches and pane
    /// activation — fires a focus event to the PTY. Useful for
    /// hosts that genuinely want per-pane focus tracking
    /// (e.g. multi-terminal IDE views where each terminal is its
    /// own session and the user wants explicit per-session focus
    /// signalling).</summary>
    Control,
}
