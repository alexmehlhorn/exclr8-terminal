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
}
