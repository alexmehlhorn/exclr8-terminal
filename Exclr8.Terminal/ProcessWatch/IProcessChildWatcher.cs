using System;

namespace Exclr8.Terminal.ProcessWatch;

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
///     it before raising <see cref="TreeChanged"/>.</item>
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
    /// <summary>Something in the watched subtree changed — a child
    /// was created (<see cref="ProcessTreeChangeKind.Created"/>) or a
    /// watched pid exited (<see cref="ProcessTreeChangeKind.Exited"/>).
    /// Fires on a backend thread; callers must marshal to UI if they
    /// touch UI state.</summary>
    event Action<ProcessTreeChange>? TreeChanged;

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
