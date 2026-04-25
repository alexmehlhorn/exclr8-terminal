using System;

namespace Exclr8.Terminal;

/// <summary>
/// Pluggable diagnostic sink for the terminal control and its
/// subsystems. Library internals write to <see cref="Error"/> when
/// something goes wrong in a non-fatal way (WMI query failure, kevent
/// ESRCH race, subscriber throws, etc.). Host apps that prefer a
/// structured logger over stderr can redirect by assigning to
/// <see cref="Error"/>; the default writes to
/// <see cref="Console.Error"/> so a freshly-dropped-in library still
/// surfaces problems without any host plumbing.
/// </summary>
public static class TerminalLog
{
    /// <summary>Called once per non-fatal error. Message is already
    /// tagged with the subsystem it came from.</summary>
    public static Action<string> Error { get; set; } =
        msg => Console.Error.WriteLine(msg);

    /// <summary>Called for protocol-level diagnostic events
    /// (unhandled CSI / OSC / DCS / ESC / DEC modes). Off by default
    /// because well-behaved shells emit a steady drizzle of vendor
    /// extensions and obscure private modes that we intentionally
    /// don't implement; logging every one would drown real signal.
    /// Hosts debugging compatibility issues set
    /// <see cref="EnableProtocolTrace"/> = true and read the
    /// messages here.</summary>
    public static Action<string> Trace { get; set; } =
        msg => Console.Error.WriteLine(msg);

    /// <summary>Gate for protocol-trace messages. False = silent
    /// (the default), true = every unhandled sequence flows through
    /// <see cref="Trace"/>.</summary>
    public static bool EnableProtocolTrace { get; set; }

    /// <summary>Internal helper — gate on the flag before allocating
    /// the formatted message. Hot path: an unrecognised CSI lands
    /// here on every keystroke for some shells, so the flag check
    /// must short-circuit before any string formatting.</summary>
    internal static void TraceProtocol(string message)
    {
        if (!EnableProtocolTrace) return;
        try { Trace(message); } catch { /* never throw out of the parser */ }
    }
}
