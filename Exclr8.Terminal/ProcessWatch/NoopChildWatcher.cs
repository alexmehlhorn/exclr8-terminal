using System;

namespace Exclr8.Terminal.ProcessWatch;

/// <summary>Placeholder backend for platforms where kernel-level
/// process-start events need elevated privileges we don't have
/// (Linux without <c>CAP_NET_ADMIN</c>). Accepts Watch calls,
/// never fires. Subscribers on those hosts simply never see
/// <see cref="TerminalControl.ProcessTreeChanged"/>; callers that
/// care can fall back to their own triggered scans.</summary>
internal sealed class NoopChildWatcher : IProcessChildWatcher
{
    public event Action<ProcessTreeChange>? TreeChanged { add { } remove { } }
    public bool IsEventDriven => false;
    public void Watch(int parentPid) { }
    public void Unwatch(int parentPid) { }
    public void Dispose() { }
}
