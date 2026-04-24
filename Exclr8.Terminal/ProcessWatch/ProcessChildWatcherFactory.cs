using System;

namespace Exclr8.Terminal.ProcessWatch;

/// <summary>OS-appropriate picker for an
/// <see cref="IProcessChildWatcher"/>. Try the native
/// event-driven backend; on construction failure (WMI disabled,
/// kqueue() errno, ...) fall back to the no-op watcher so the
/// control can keep publishing (empty) <see cref="TerminalControl.ProcessTreeChanged"/>
/// without requiring callers to branch on OS.</summary>
internal static class ProcessChildWatcherFactory
{
    public static IProcessChildWatcher Create()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return new WmiChildWatcher();
            if (OperatingSystem.IsMacOS())
                return new KQueueChildWatcher();
        }
        catch (Exception ex)
        {
            TerminalLog.Error($"[ProcessChildWatcherFactory] native backend unavailable: {ex.Message}");
        }
        return new NoopChildWatcher();
    }
}
