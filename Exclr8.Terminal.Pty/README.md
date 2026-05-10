# Exclr8.Terminal.Pty

[![NuGet](https://img.shields.io/nuget/v/Exclr8.Terminal.Pty.svg)](https://www.nuget.org/packages/Exclr8.Terminal.Pty)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)

Spawn-and-go PTY adapter for [`Exclr8.Terminal`](https://www.nuget.org/packages/Exclr8.Terminal).

`Exclr8.Terminal` is intentionally PTY-agnostic — that's what makes
SSH channels, replay streams, and custom transports work. But hosts
that want a local shell don't need to reimplement the same ~500 lines
of glue (read loop, writer-lock-serialised input, resize forwarding,
exit-code propagation, dispose ordering) every time. This package
ships that glue.

```sh
dotnet add package Exclr8.Terminal.Pty
```

Pulls `Exclr8.Terminal` and [`Porta.Pty`](https://www.nuget.org/packages/Porta.Pty)
transitively. `Porta.Pty` handles the cross-platform PTY layer
(macOS / Linux pty + Windows ConPTY) under the hood.

## Quick start

```csharp
using Exclr8.Terminal;
using Exclr8.Terminal.Pty;
using Porta.Pty;

var terminal = new TerminalControl();
// ... add the control to your view tree somehow ...

var adapter = new PtyTerminalAdapter(terminal);

// Wait for the first Resized so the shell starts at the actual cell
// grid (not 80x24 default → SIGWINCH on first paint).
terminal.Resized += async (_, size) =>
{
    await adapter.StartAsync(new PtyOptions
    {
        Name        = "xterm-256color",
        App         = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/zsh",
        Cwd         = Environment.GetEnvironmentVariable("HOME") ?? "/",
        Cols        = size.Cols,
        Rows        = size.Rows,
        CommandLine = Array.Empty<string>(),
        Environment = new Dictionary<string, string>
        {
            ["TERM"]      = "xterm-256color",
            ["COLORTERM"] = "truecolor",
        },
    });
};

adapter.ProcessExited += (_, e) => Console.WriteLine($"shell exited code={e.ExitCode}");

// On window close:
await adapter.DisposeAsync();
```

That's it. The adapter:

- Calls `terminal.PrepareForNewSession()` before spawning so a
  recycled control doesn't carry SGR / scrollback state from the
  previous session.
- Sets `terminal.RootProcessId = pty.Pid` so the process-tree watcher
  hooks the spawned subtree automatically.
- Subscribes to `terminal.Input` and forwards through a writer lock
  (mandatory on Windows — ConPTY corrupts the input pipe under
  concurrent writes).
- Subscribes to `terminal.Resized` and forwards to `pty.Resize`.
- Runs a 16 KB-buffered async read loop, marshalling bytes onto the
  Avalonia UI thread before calling `terminal.Write`.
- Surfaces `pty.ProcessExited` so you can drive UI on shell death.
- Tears down everything in `DisposeAsync` — read loop cancellation,
  await, handler unsubscribe, connection dispose. Idempotent.

## API surface

| Member | Purpose |
|---|---|
| `new PtyTerminalAdapter(terminal)` | Bind an adapter to an existing `TerminalControl`. Spawns nothing yet. |
| `StartAsync(PtyOptions, ct)` | Spawn a Porta.Pty connection and wire it up. Idempotent re-call disposes the previous one first. |
| `Pid` | OS pid of the spawned shell, or `null` when not live. |
| `IsAlive` | `true` between successful `StartAsync` and read loop completion / dispose. |
| `ProcessExited` | `EventHandler<PtyExitedEventArgs>` — fires when the shell exits. May fire on a worker thread; marshal before touching UI. |
| `DisposeAsync()` | Tear down. Idempotent, safe from any thread. |

Beyond this, use the underlying `terminal` for everything else —
selection, search, copy/paste, theming, fonts, focus, recovery
primitives. The adapter only owns the PTY plumbing.

## Threading

- `StartAsync` should be called on the UI thread (it touches
  `TerminalControl` properties and subscribes events).
- The read loop runs on a `Task.Run` background task; it marshals
  bytes onto `Dispatcher.UIThread` before `terminal.Write`.
- `ProcessExited` fires on whatever thread Porta.Pty surfaces the
  exit on (typically a wait worker thread). Marshal it before
  touching UI state.

## When *not* to use this package

Skip it and wire `terminal.Write` / `Input` / `Resized` directly when:

- Your bytes come from an SSH channel (`SSH.NET`, `libssh`).
- You're driving a recorded session (asciicast / ttyrec / custom).
- The "PTY" is in-memory (replay tests, agent-driven sessions).
- You want a different PTY library than Porta.Pty.
- You're building a multiplexed transport (one PTY → N terminal panes).

The core `Exclr8.Terminal` covers all those cases without bringing
Porta.Pty along for the ride.

## License

MIT (matches the core). `Porta.Pty` is MIT (Microsoft OSS); same
licence, no compatibility friction. See the root [`LICENSE`](../LICENSE)
for the full text and the upstream Avalonia / xterm.js attributions
inherited from the core package.
