using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Porta.Pty;

namespace Exclr8.Terminal.Pty;

/// <summary>
/// Wires a <see cref="TerminalControl"/> to a Porta.Pty-backed
/// pseudoterminal in one <see cref="StartAsync"/> call. Owns the
/// background read loop, serialised input writer, resize forwarding,
/// process-exit propagation, and ordered async dispose so a host
/// doesn't reimplement the same ~500 lines of glue every time.
/// </summary>
/// <remarks>
/// <para>
/// The core <see cref="TerminalControl"/> stays PTY-agnostic on
/// purpose — SSH channels, replay streams, recorded sessions and
/// custom transports all wire <c>Write()</c> / <c>Input</c> /
/// <c>Resized</c> directly. This adapter is the optional opt-in
/// for hosts that just want a local shell.
/// </para>
/// <para>
/// Lifecycle: construct → <see cref="StartAsync"/> → use the terminal →
/// await <see cref="DisposeAsync"/>. Re-calling <see cref="StartAsync"/>
/// on a live adapter disposes the previous connection first, so
/// hosts that recycle a single terminal across sessions can do so
/// without manual teardown.
/// </para>
/// </remarks>
public sealed class PtyTerminalAdapter : IAsyncDisposable
{
    // Win32 ConPTY corrupts the input pipe under concurrent writes
    // (verified by VibeCoder pre-bracketed-paste fixes); this lock
    // serialises every byte reaching pty.WriterStream regardless of
    // how many keystroke / paste / programmatic input events fire
    // off the UI dispatcher in close succession.
    private readonly object _writerLock = new();

    private readonly TerminalControl _terminal;

    private IPtyConnection? _pty;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private bool _wired;
    // Volatile so the read loop / dispatcher post lambda observe the
    // Dispose-side write promptly. The .NET memory model lets a plain
    // bool read see a stale value indefinitely on weakly-ordered
    // architectures (Apple Silicon, ARM Linux); without the fence the
    // pump or post can dispatch into a half-disposed adapter.
    private volatile bool _disposed;

    /// <summary>Create an adapter bound to a control. Does not spawn
    /// anything until <see cref="StartAsync"/> is called.</summary>
    public PtyTerminalAdapter(TerminalControl terminal)
    {
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
    }

    /// <summary>OS process id of the spawned shell, or null if no PTY
    /// is currently live.</summary>
    public int? Pid => _pty?.Pid;

    /// <summary>True between a successful <see cref="StartAsync"/> and
    /// either the read loop exiting (process death / pipe close) or
    /// <see cref="DisposeAsync"/>.</summary>
    public bool IsAlive => _pty != null && _readTask is { IsCompleted: false };

    /// <summary>Fired on the thread Porta.Pty surfaces the exit on
    /// (typically a worker thread). Marshal to your UI thread before
    /// touching UI state. <c>ExitCode</c> mirrors the underlying
    /// process exit code.</summary>
    public event EventHandler<PtyExitedEventArgs>? ProcessExited;

    /// <summary>Spawn a Porta.Pty connection with <paramref name="options"/>
    /// and wire it to the bound terminal: read loop, input
    /// (writer-lock-serialised), resize, root-pid tracking. Returns
    /// once the PTY is live and the read pump is scheduled. Idempotent
    /// — calling twice tears down the prior connection first.</summary>
    public async Task StartAsync(PtyOptions options, CancellationToken ct = default)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pty is not null) await DisposeConnectionAsync().ConfigureAwait(false);

        // Keeps a previous session's scrollback / SGR pen / DEC modes
        // from leaking into the freshly-spawned shell. The control's
        // own README recommends this as the canonical "before connecting
        // a freshly-spawned PTY" call; bake it in so hosts can't forget.
        _terminal.PrepareForNewSession();

        var pty = await PtyProvider.SpawnAsync(options, ct).ConfigureAwait(false);
        _pty = pty;

        // Hooks the process-tree watcher to the spawned subtree for free.
        // Hosts that want "running command" badges or descendant tracking
        // get them without any extra wiring.
        _terminal.RootProcessId = pty.Pid;

        WireHandlers();
        pty.ProcessExited += OnProcessExited;

        _readCts  = new CancellationTokenSource();
        _readTask = Task.Run(() => ReadLoopAsync(pty, _readCts.Token));
    }

    private void WireHandlers()
    {
        if (_wired) return;
        _terminal.Input   += OnTerminalInput;
        _terminal.Resized += OnTerminalResized;
        _wired = true;
    }

    private void UnwireHandlers()
    {
        if (!_wired) return;
        _terminal.Input   -= OnTerminalInput;
        _terminal.Resized -= OnTerminalResized;
        _wired = false;
    }

    private void OnTerminalInput(object? sender, ReadOnlyMemory<byte> bytes)
        => Send(bytes.Span);

    /// <summary>Write bytes to the PTY through the same writer-lock
    /// that serialises typed input. Use for programmatic injection
    /// the host needs alongside keyboard input — e.g. terminal-emitted
    /// DSR/DA replies (TerminalControl.Output), file-drop payloads
    /// pasted as text, or host-driven command injection. Returns
    /// <c>true</c> on success, <c>false</c> if no PTY is attached or
    /// the underlying stream rejected the write (process exiting,
    /// pipe torn down). The pipe-broken case is logged via
    /// <see cref="TerminalLog"/> rather than thrown so a stray late
    /// write doesn't escape to the host's UI thread.</summary>
    public bool Send(ReadOnlySpan<byte> bytes)
    {
        var pty = _pty;
        if (pty is null || bytes.IsEmpty) return false;
        try
        {
            lock (_writerLock)
            {
                pty.WriterStream.Write(bytes);
                pty.WriterStream.Flush();
            }
            return true;
        }
        catch (Exception ex)
        {
            // Pipe broken / connection torn down mid-write. The read
            // loop will surface ProcessExited shortly; nothing useful
            // to do at the input handler beyond logging so a single
            // bad write doesn't escape to the host's keyboard handler.
            TerminalLog.Error($"[PtyTerminalAdapter] write failed: {ex.Message}");
            return false;
        }
    }

    private void OnTerminalResized(object? sender, (int Cols, int Rows) size)
        => Resize(size.Cols, size.Rows);

    /// <summary>Resize the underlying PTY directly. The adapter
    /// already forwards <c>Terminal.Resized</c> events automatically;
    /// this overload is for hosts that need to fire SIGWINCH without
    /// a real layout change — e.g. catching up after a resize that
    /// arrived between option-build and <see cref="StartAsync"/>, or
    /// nudging a running TUI to redraw on cell focus. Returns
    /// <c>true</c> on success, <c>false</c> when no PTY is attached
    /// or the resize call threw.</summary>
    public bool Resize(int cols, int rows)
    {
        var pty = _pty;
        if (pty is null || cols <= 0 || rows <= 0) return false;
        try { pty.Resize(cols, rows); return true; }
        catch (Exception ex)
        {
            TerminalLog.Error($"[PtyTerminalAdapter] resize failed: {ex.Message}");
            return false;
        }
    }

    private async Task ReadLoopAsync(IPtyConnection pty, CancellationToken ct)
    {
        // 16 KB matches VibeCoder's read size. Smaller and ConPTY
        // chunks more aggressively and we burn dispatch overhead per
        // chunk; larger and we hold the read open longer for
        // negligible throughput gain on interactive workloads.
        var pool   = ArrayPool<byte>.Shared;
        var buffer = pool.Rent(16384);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n;
                try
                {
                    n = await pty.ReaderStream
                        .ReadAsync(buffer.AsMemory(0, buffer.Length), ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (IOException)                { break; } // pipe closed — process exited
                catch (ObjectDisposedException)    { break; } // disposed mid-read

                if (n == 0) break;

                // Copy out before posting: ArrayPool.Return must wait for
                // every consumer of the slice. Posting Write(chunk) defers
                // the consume to the UI thread, so the pooled buffer
                // could be reused for the next read while bytes are still
                // in flight. A per-chunk allocation costs little against
                // the dispatch overhead.
                var chunk = new byte[n];
                System.Buffer.BlockCopy(buffer, 0, chunk, 0, n);

                Dispatcher.UIThread.Post(() =>
                {
                    // Disposed between Post and dispatch — drop the bytes
                    // rather than feed a torn-down terminal.
                    if (!_disposed) _terminal.Write(chunk);
                });
            }
        }
        finally
        {
            pool.Return(buffer);
        }
    }

    private void OnProcessExited(object? sender, PtyExitedEventArgs e)
        => ProcessExited?.Invoke(this, e);

    /// <summary>Tear down the read loop, unsubscribe terminal events,
    /// dispose the PTY connection. Idempotent. Safe to call from any
    /// thread.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await DisposeConnectionAsync().ConfigureAwait(false);
    }

    private async Task DisposeConnectionAsync()
    {
        var pty  = _pty;
        var cts  = _readCts;
        var task = _readTask;

        _pty      = null;
        _readCts  = null;
        _readTask = null;

        UnwireHandlers();
        if (pty is not null) pty.ProcessExited -= OnProcessExited;

        try { cts?.Cancel(); } catch { /* already disposed */ }

        if (task is not null)
        {
            // The read loop catches OperationCanceledException itself,
            // so awaiting shouldn't throw. Defensive try/catch keeps a
            // pathological exception in user code from leaking out of
            // dispose.
            try { await task.ConfigureAwait(false); }
            catch (Exception ex)
            {
                TerminalLog.Error($"[PtyTerminalAdapter] read loop teardown: {ex.Message}");
            }
        }

        cts?.Dispose();
        try { pty?.Dispose(); }
        catch (Exception ex)
        {
            TerminalLog.Error($"[PtyTerminalAdapter] pty dispose: {ex.Message}");
        }
    }
}
