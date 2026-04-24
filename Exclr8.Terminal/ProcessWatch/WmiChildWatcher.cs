using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.Versioning;

namespace Exclr8.Terminal.ProcessWatch;

/// <summary>
/// Windows implementation of <see cref="IProcessChildWatcher"/>
/// backed by WMI's <c>__InstanceCreationEvent</c> /
/// <c>__InstanceDeletionEvent</c> on <c>Win32_Process</c>, scoped
/// per-parent-pid. One <see cref="ManagementEventWatcher"/> per
/// watched pid; the WMI service handles the polling under the
/// hood so we pay nothing when nothing fires.
/// </summary>
/// <remarks>
/// Event payload's <c>TargetInstance</c> carries <c>Name</c>,
/// <c>CommandLine</c>, and <c>ProcessId</c> directly — no follow-up
/// process lookup required.
///
/// <para><b>Latency:</b> <c>WITHIN 1</c> means up to ~1 second from
/// fork to notification. Acceptable for UI badge updates; would
/// need a different API (ETW Microsoft-Windows-Kernel-Process) for
/// sub-millisecond.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WmiChildWatcher : IProcessChildWatcher
{
    private readonly object _lock = new();
    private readonly Dictionary<int, Entry> _watchers = new();
    private bool _disposed;

    public event Action<ProcessTreeChange>? TreeChanged;

    public bool IsEventDriven => true;

    public void Watch(int parentPid)
    {
        if (parentPid <= 0) return;
        lock (_lock)
        {
            if (_disposed) return;
            if (_watchers.ContainsKey(parentPid)) return;

            var entry = new Entry();
            try
            {
                entry.CreationWatcher = CreateCreationWatcher(parentPid);
                entry.DeletionWatcher = CreateDeletionWatcher(parentPid);
                entry.CreationWatcher.Start();
                entry.DeletionWatcher.Start();
                _watchers[parentPid] = entry;
            }
            catch (Exception ex)
            {
                // WMI can be flaky (service disabled, quota issues).
                // Stop cleanly and leave the pid unwatched — caller
                // can fall back to triggered scans.
                TerminalLog.Error($"[WmiChildWatcher] Watch({parentPid}) failed: {ex.Message}");
                entry.Dispose();
            }
        }
    }

    public void Unwatch(int parentPid)
    {
        Entry? entry = null;
        lock (_lock)
        {
            if (_watchers.Remove(parentPid, out var e)) entry = e;
        }
        entry?.Dispose();
    }

    private ManagementEventWatcher CreateCreationWatcher(int parentPid)
    {
        var query = new WqlEventQuery(
            "__InstanceCreationEvent",
            TimeSpan.FromSeconds(1),
            $"TargetInstance ISA 'Win32_Process' AND TargetInstance.ParentProcessId = {parentPid}");
        var w = new ManagementEventWatcher(query);
        w.EventArrived += (_, e) =>
        {
            try
            {
                using var target = (ManagementBaseObject)e.NewEvent["TargetInstance"];
                if (target == null) return;
                int childPid  = unchecked((int)Convert.ToUInt32(target["ProcessId"] ?? 0u));
                int parent    = unchecked((int)Convert.ToUInt32(target["ParentProcessId"] ?? 0u));
                string? name  = target["Name"] as string;
                string? cmd   = target["CommandLine"] as string;
                if (childPid > 0)
                {
                    TreeChanged?.Invoke(new ProcessTreeChange(
                        Kind:        ProcessTreeChangeKind.Created,
                        Pid:         childPid,
                        ParentPid:   parent,
                        Name:        name,
                        CommandLine: cmd));
                }
            }
            catch (Exception ex)
            {
                TerminalLog.Error($"[WmiChildWatcher] creation-event dispatch: {ex.Message}");
            }
        };
        return w;
    }

    private ManagementEventWatcher CreateDeletionWatcher(int parentPid)
    {
        // We get deletion events for our watched parent itself too,
        // which is useful so callers can clear the cell state when
        // the shell exits without going through Dispose paths.
        var query = new WqlEventQuery(
            "__InstanceDeletionEvent",
            TimeSpan.FromSeconds(1),
            $"TargetInstance ISA 'Win32_Process' AND " +
            $"(TargetInstance.ParentProcessId = {parentPid} OR TargetInstance.ProcessId = {parentPid})");
        var w = new ManagementEventWatcher(query);
        w.EventArrived += (_, e) =>
        {
            try
            {
                using var target = (ManagementBaseObject)e.NewEvent["TargetInstance"];
                if (target == null) return;
                int pid = unchecked((int)Convert.ToUInt32(target["ProcessId"] ?? 0u));
                if (pid > 0)
                {
                    TreeChanged?.Invoke(new ProcessTreeChange(
                        Kind:        ProcessTreeChangeKind.Exited,
                        Pid:         pid,
                        ParentPid:   0,
                        Name:        null,
                        CommandLine: null));
                }
            }
            catch (Exception ex)
            {
                TerminalLog.Error($"[WmiChildWatcher] deletion-event dispatch: {ex.Message}");
            }
        };
        return w;
    }

    public void Dispose()
    {
        Entry[] entries;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            entries = new Entry[_watchers.Count];
            _watchers.Values.CopyTo(entries, 0);
            _watchers.Clear();
        }
        foreach (var e in entries) e.Dispose();
    }

    private sealed class Entry : IDisposable
    {
        public ManagementEventWatcher? CreationWatcher;
        public ManagementEventWatcher? DeletionWatcher;

        public void Dispose()
        {
            try { CreationWatcher?.Stop(); } catch { }
            try { DeletionWatcher?.Stop(); } catch { }
            CreationWatcher?.Dispose();
            DeletionWatcher?.Dispose();
            CreationWatcher = null;
            DeletionWatcher = null;
        }
    }
}
