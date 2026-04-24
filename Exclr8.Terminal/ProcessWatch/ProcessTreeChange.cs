namespace Exclr8.Terminal.ProcessWatch;

/// <summary>What happened in the terminal's process tree.</summary>
public enum ProcessTreeChangeKind
{
    /// <summary>A new process was spawned somewhere in the watched
    /// subtree rooted at <see cref="TerminalControl.RootProcessId"/>.</summary>
    Created,
    /// <summary>A watched process exited. Optional — not every OS
    /// backend reports exits, so subscribers should not rely on
    /// seeing one for every <see cref="Created"/>.</summary>
    Exited,
}

/// <summary>Generic notification that the process tree rooted at
/// <see cref="TerminalControl.RootProcessId"/> has changed.
/// Subscribers interpret <see cref="Pid"/> / <see cref="Name"/> /
/// <see cref="CommandLine"/> however they need — the terminal itself
/// has no opinion about what the running process means.</summary>
/// <remarks>
/// <see cref="Name"/> and <see cref="CommandLine"/> are best-effort.
/// Windows (WMI) typically fills both. macOS (kqueue) fills
/// <see cref="Name"/> (short command name via <c>proc_name</c>);
/// <see cref="CommandLine"/> is null. Subscribers that need
/// guaranteed detail should do their own lookup from
/// <see cref="Pid"/>.
/// </remarks>
public readonly record struct ProcessTreeChange(
    ProcessTreeChangeKind Kind,
    int                   Pid,
    int                   ParentPid,
    string?               Name,
    string?               CommandLine);
