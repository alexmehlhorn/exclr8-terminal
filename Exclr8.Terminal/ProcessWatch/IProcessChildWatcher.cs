using System;

namespace Exclr8.Terminal.ProcessWatch;

/// <summary>Internal payload of a child-creation notification from a
/// per-OS backend. <see cref="Name"/> and <see cref="CommandLine"/>
/// are best-effort — backends fill them in when cheap, leave null
/// otherwise; <see cref="TerminalControl"/> surfaces them to
/// subscribers via <see cref="ProcessTreeChange"/>.</summary>
internal readonly record struct ProcessChildEvent(
    int      ChildPid,
    int      ParentPid,
    string?  Name,
    string?  CommandLine);

/// <summary>
/// OS-level "my process spawned a child" notifications without
/// polling the process table. Used by <see cref="TerminalControl"/>
/// to power its <see cref="TerminalControl.ProcessTreeChanged"/>
/// event so subscribers can react to process-tree changes inside
/// the terminal's shell without tree-walking.
///
/// Per-OS backends:
/// <list type="bullet">
///   <item>Windows — WMI <c>__InstanceCreationEvent</c> subscription
///     scoped to <c>ParentProcessId = &lt;watched&gt;</c>. WMI service
///     handles the polling internally; caller is only woken on
///     matching events. Name + CommandLine arrive in the event.</item>
///   <item>macOS — <c>kqueue</c> with <c>EVFILT_PROC</c> +
///     <c>NOTE_FORK</c>. Fires on fork, but the event doesn't carry
///     the child pid — the backend follows up with
///     <c>proc_listpids(PROC_LISTCHILDRENPIDS, …)</c> to resolve
///     it before raising <see cref="ChildCreated"/>.</item>
///   <item>Linux — unprivileged access to the kernel's proc
///     connector requires <c>CAP_NET_ADMIN</c>, which is out of
///     reach for a normal user app. The no-op backend is used
///     there.</item>
/// </list>
///
/// To catch grandchildren, the control calls <see cref="Watch"/> on
/// each newly-reported child pid from inside its own handler.
/// </summary>
internal interface IProcessChildWatcher : IDisposable
{
    /// <summary>A new child of a watched pid was observed.</summary>
    event Action<ProcessChildEvent>? ChildCreated;

    /// <summary>A watched pid (or one of its watched descendants)
    /// has exited. Optional — backends may or may not surface it;
    /// callers should not assume every exit is reported.</summary>
    event Action<int>? ProcessExited;

    /// <summary>Start watching for new children of the given pid.
    /// Safe to call multiple times with the same pid — idempotent.</summary>
    void Watch(int parentPid);

    /// <summary>Stop watching a previously-registered pid. Safe to
    /// call on an unknown pid — no-op.</summary>
    void Unwatch(int parentPid);

    /// <summary>True when this backend actually observes events
    /// (Windows, macOS). False for platforms that fall back to
    /// the no-op backend.</summary>
    bool IsEventDriven { get; }
}
