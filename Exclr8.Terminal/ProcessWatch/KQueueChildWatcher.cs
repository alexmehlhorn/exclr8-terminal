using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Exclr8.Terminal.ProcessWatch;

/// <summary>
/// macOS implementation of <see cref="IProcessChildWatcher"/> using
/// <c>kqueue</c> / <c>EVFILT_PROC</c>. For each watched pid we
/// register a kevent filter that fires on fork / exec / exit. The
/// NOTE_FORK notification doesn't carry the new child pid — we
/// follow up with <c>proc_listchildpids</c> to find it and raise
/// <see cref="ChildCreated"/> for any pid that wasn't in the
/// previous snapshot.
/// </summary>
/// <remarks>
/// One shared kqueue fd with a background pump thread; one kevent
/// registration per watched parent pid. Unprivileged — same-user
/// processes only.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class KQueueChildWatcher : IProcessChildWatcher
{
    // kqueue constants — from /usr/include/sys/event.h
    private const short EVFILT_PROC = -5;
    private const ushort EV_ADD     = 0x0001;
    private const ushort EV_DELETE  = 0x0002;
    private const ushort EV_ENABLE  = 0x0004;
    private const uint NOTE_EXIT    = 0x80000000;
    private const uint NOTE_FORK    = 0x40000000;
    private const uint NOTE_EXEC    = 0x20000000;
    private const uint NOTE_TRACK   = 0x00000001;

    // proc_listpidspath constants — from /usr/include/libproc.h.
    // PROC_LISTCHILDRENPIDS (3) returns immediate children of pid.
    private const uint PROC_LISTCHILDRENPIDS = 3;

    private readonly int _kq;
    private readonly Thread _pumpThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, byte> _watched = new();
    private readonly Dictionary<int, HashSet<int>> _lastChildren = new();
    private readonly object _childrenLock = new();
    private bool _disposed;

    public event Action<ProcessChildEvent>? ChildCreated;
    public event Action<int>? ProcessExited;

    public bool IsEventDriven => true;

    public KQueueChildWatcher()
    {
        _kq = kqueue();
        if (_kq < 0)
            throw new InvalidOperationException($"kqueue() failed, errno={Marshal.GetLastPInvokeError()}");
        _pumpThread = new Thread(Pump) { Name = "KQueueChildWatcher", IsBackground = true };
        _pumpThread.Start();
    }

    public void Watch(int parentPid)
    {
        if (parentPid <= 0 || _disposed) return;
        if (!_watched.TryAdd(parentPid, 0)) return;

        var change = new kevent_s
        {
            ident  = (nint)parentPid,
            filter = EVFILT_PROC,
            flags  = EV_ADD | EV_ENABLE,
            fflags = NOTE_FORK | NOTE_EXEC | NOTE_EXIT | NOTE_TRACK,
            data   = 0,
            udata  = IntPtr.Zero,
        };
        int rc;
        unsafe
        {
            var changes = stackalloc kevent_s[1] { change };
            rc = kevent(_kq, changes, 1, null, 0, null);
        }
        if (rc < 0)
        {
            Console.Error.WriteLine($"[KQueueChildWatcher] kevent ADD({parentPid}) failed, errno={Marshal.GetLastPInvokeError()}");
            _watched.TryRemove(parentPid, out _);
            return;
        }
        lock (_childrenLock) _lastChildren[parentPid] = ListChildrenOf(parentPid);
    }

    public void Unwatch(int parentPid)
    {
        if (!_watched.TryRemove(parentPid, out _)) return;
        var change = new kevent_s
        {
            ident  = (nint)parentPid,
            filter = EVFILT_PROC,
            flags  = EV_DELETE,
        };
        unsafe
        {
            var changes = stackalloc kevent_s[1] { change };
            kevent(_kq, changes, 1, null, 0, null);
        }
        lock (_childrenLock) _lastChildren.Remove(parentPid);
    }

    private void Pump()
    {
        var ts = new timespec { tv_sec = 1, tv_nsec = 0 }; // 1 s timeout so we can check cancel
        Span<kevent_s> buf = stackalloc kevent_s[16];
        while (!_cts.IsCancellationRequested)
        {
            int rc;
            unsafe
            {
                // ts is a local struct — already at a fixed stack
                // address from the CLR's POV, so no 'fixed' needed.
                fixed (kevent_s* pBuf = buf)
                {
                    timespec t = ts;
                    rc = kevent(_kq, null, 0, pBuf, buf.Length, &t);
                }
            }
            if (rc < 0) break;
            for (int i = 0; i < rc; i++)
            {
                var ev  = buf[i];
                int pid = (int)ev.ident;
                uint f  = ev.fflags;

                if ((f & NOTE_EXIT) != 0)
                {
                    try { ProcessExited?.Invoke(pid); }
                    catch (Exception ex) { Console.Error.WriteLine($"[KQueueChildWatcher] exit dispatch: {ex.Message}"); }
                    _watched.TryRemove(pid, out _);
                    lock (_childrenLock) _lastChildren.Remove(pid);
                }
                if ((f & NOTE_FORK) != 0 || (f & NOTE_EXEC) != 0)
                {
                    HashSet<int> current = ListChildrenOf(pid);
                    HashSet<int> prev;
                    lock (_childrenLock)
                    {
                        _lastChildren.TryGetValue(pid, out prev!);
                        prev ??= new HashSet<int>();
                        _lastChildren[pid] = current;
                    }
                    foreach (var child in current)
                    {
                        if (prev.Contains(child)) continue;
                        string? name = LookupProcessName(child);
                        try
                        {
                            ChildCreated?.Invoke(new ProcessChildEvent(
                                ChildPid:    child,
                                ParentPid:   pid,
                                Name:        name,
                                CommandLine: null));
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[KQueueChildWatcher] fork dispatch: {ex.Message}");
                        }
                    }
                }
            }
        }
    }

    /// <summary>Returns the immediate children of <paramref name="pid"/>
    /// via <c>proc_listpids(PROC_LISTCHILDRENPIDS, ...)</c>. Empty
    /// set on failure — we'd rather miss a notification than crash.</summary>
    private static HashSet<int> ListChildrenOf(int pid)
    {
        var set = new HashSet<int>();
        // First call with null to learn the required buffer size.
        int need = proc_listpids(PROC_LISTCHILDRENPIDS, (uint)pid, IntPtr.Zero, 0);
        if (need <= 0) return set;
        // Over-allocate a bit so a racing fork doesn't truncate us.
        int capacity = Math.Max(need / sizeof(int) + 16, 32);
        IntPtr buf = Marshal.AllocHGlobal(capacity * sizeof(int));
        try
        {
            int got = proc_listpids(PROC_LISTCHILDRENPIDS, (uint)pid, buf, capacity * sizeof(int));
            if (got <= 0) return set;
            int count = got / sizeof(int);
            for (int i = 0; i < count; i++)
            {
                int childPid = Marshal.ReadInt32(buf, i * sizeof(int));
                if (childPid > 0) set.Add(childPid);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return set;
    }

    /// <summary>Short name (e.g. "zsh" / "vim") for a pid, or null
    /// on failure.</summary>
    private static string? LookupProcessName(int pid)
    {
        var buf = new byte[256];
        int n;
        unsafe
        {
            fixed (byte* p = buf) n = proc_name(pid, p, (uint)buf.Length);
        }
        if (n <= 0) return null;
        int len = Array.IndexOf(buf, (byte)0, 0, Math.Min(n, buf.Length));
        if (len < 0) len = n;
        return System.Text.Encoding.UTF8.GetString(buf, 0, len);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { close(_kq); } catch { }
        try { _pumpThread.Join(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }

    // ---- P/Invoke surface ----

    [StructLayout(LayoutKind.Sequential)]
    private struct kevent_s
    {
        public nint  ident;    // uintptr_t
        public short filter;
        public ushort flags;
        public uint  fflags;
        public nint  data;     // intptr_t
        public IntPtr udata;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct timespec
    {
        public long tv_sec;
        public long tv_nsec;
    }

    [DllImport("libSystem", SetLastError = true, EntryPoint = "kqueue")]
    private static extern int kqueue();

    [DllImport("libSystem", SetLastError = true, EntryPoint = "close")]
    private static extern int close(int fd);

    [DllImport("libSystem", SetLastError = true, EntryPoint = "kevent")]
    private static extern unsafe int kevent(
        int kq,
        kevent_s* changelist, int nchanges,
        kevent_s* eventlist,  int nevents,
        timespec* timeout);

    [DllImport("libproc", SetLastError = true, EntryPoint = "proc_listpids")]
    private static extern int proc_listpids(uint type, uint typeInfo, IntPtr buffer, int buffersize);

    [DllImport("libproc", SetLastError = true, EntryPoint = "proc_name")]
    private static extern unsafe int proc_name(int pid, byte* buffer, uint buffersize);
}
