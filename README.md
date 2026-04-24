# Exclr8.Terminal

A native Avalonia terminal control for .NET. Drop it into a view, feed
it the bytes your process produces on one side, and forward the bytes
it wants written back on the other. You get a fully-featured terminal —
parser, renderer, selection, search, scrollback, the works — with no
process-spawning or PTY plumbing baked in.

## What it does

- **VT500-class escape-sequence parser** — ESC / CSI / OSC / DCS, with
  8-bit C1 sequence starts, UTF-8 assembly, and DEC private modes
  (DECSTBM scroll region, DECAWM auto-wrap, DECOM origin, DECSCNM
  reverse video, DECCKM / DECKPAM application modes, IRM insert mode,
  LNM line-feed mode).
- **Full SGR styling** — 24-bit RGB, the standard 256-colour palette,
  bold, italic, underline, strikethrough, inverse, dim, blink.
- **Unicode + wide characters** — CJK, emoji, astral-plane runes
  handled correctly in rendering, selection, and find.
- **OSC 8 hyperlinks** — clickable URLs emitted by the running process
  surface as a `HyperlinkClicked` event.
- **Scrollback** with pixel-smooth wheel / trackpad scrolling and an
  auto-hiding scrollbar you can grab.
- **Find-in-buffer** — case-insensitive, debounced, runs off the UI
  thread, cancellable; navigate match-by-match.
- **Selection** — character drag, word on double-click, line on
  triple-click; absolute-anchored so scrolling doesn't smear the
  highlight.
- **Clipboard** — copy selection, paste text, paste an image from the
  clipboard (spilled to a temp file whose path is pasted). Bracketed
  paste honoured when the shell asks for it.
- **Cursor styles** via DECSCUSR (block / underline / bar, blinking or
  steady).
- **Mouse reporting** — X10 / 1000 / 1002 / 1003 modes, SGR 1006
  encoding.
- **Focus events** via DECSET 1004.
- **Process-tree watching** — optional OS-level notifications (kqueue
  on macOS, WMI on Windows) when the shell forks or a descendant
  exits. Useful for "running process" badges and session tagging.
- **Theming** — foreground, background, cursor, and ANSI palette
  overrides.
- **Font zoom** — Cmd/Ctrl +, -, 0 to bump, shrink, reset.
- **Proper disposal** — timers, watchers, in-flight search all shut
  down cleanly when the control is removed.

## Usage

Add the project as a reference, then drop `TerminalControl` into a
view.

```csharp
var terminal = new TerminalControl();
// host it in your layout wherever you want a terminal pane
```

Wire it to your byte source. The control doesn't care what that source
is — a system pseudo-terminal, an SSH channel, a replay of a recorded
stream, an in-memory echo server — as long as bytes flow both ways.

```csharp
// Bytes produced by the remote/local process go in:
terminal.Write(bytesFromProvider);

// Bytes the user typed / pasted go out:
terminal.Input += (_, payload) =>
{
    // Forward payload.Span to your byte sink
};

// Replies the terminal wants to emit back (DSR, DA, focus events):
terminal.Output += (_, payload) =>
{
    // Also forward payload.Span to your byte sink
};

// The grid changed size (font change, window resize).
// Tell your byte source so it can propagate a SIGWINCH-equivalent.
terminal.Resized += (_, size) =>
{
    Resize(size.Cols, size.Rows);
};
```

That's the minimum. Everything else is optional.

### Optional wiring

```csharp
// Open OSC 8 hyperlinks in the user's browser.
terminal.HyperlinkClicked += (_, url) => LaunchBrowser(url);

// User hit Cmd/Ctrl+Shift+F — show your find UI, then drive the
// search with these methods.
terminal.FindRequested += (_, _) => ShowFindBar();
terminal.Find(needle);          // sets the needle, debounces, scans off-thread
terminal.FindNext();            // wraps at the end
terminal.FindPrev();            // wraps at the start
terminal.CloseFind();           // clears match state
int total = terminal.MatchCount;
int current = terminal.CurrentMatch;  // 1-based

// OS-level process-tree watch. Only starts when you attach a handler,
// and only if RootProcessId is set.
terminal.RootProcessId = shellPid;
terminal.ProcessTreeChanged += change =>
{
    switch (change.Kind)
    {
        case ProcessTreeChangeKind.Created:
            // change.Pid, change.ParentPid, change.Name, change.CommandLine
            break;
        case ProcessTreeChangeKind.Exited:
            // change.Pid
            break;
    }
};

// Theming (null fields fall back to the built-in scheme).
terminal.ColorScheme = new TerminalTheme
{
    Foreground = Color.FromRgb(0xE6, 0xED, 0xF3),
    Background = Color.FromRgb(0x0D, 0x11, 0x17),
    Cursor     = Color.FromRgb(0xC9, 0xD1, 0xD9),
    // AnsiColors = ... 16-entry Color?[] to override ANSI slots
};

// Font.
terminal.FontFamily = "JetBrainsMono, Menlo, monospace";
terminal.FontSize   = 13;

// Programmatic paste (honours bracketed-paste mode automatically).
terminal.Paste(clipboardText);
```

### Built-in shortcuts

| Action | macOS | Windows / Linux |
|---|---|---|
| Copy selection | ⌘ C | Ctrl + Shift + C, or Ctrl + C when a selection exists |
| Paste | ⌘ V | Ctrl + V, or Ctrl + Shift + V |
| Select all | ⌘ A | Ctrl + Shift + A |
| Open find | ⌘ F | Ctrl + Shift + F |
| Clear scrollback | ⌘ K | Ctrl + Shift + K |
| Font bigger | ⌘ + | Ctrl + + |
| Font smaller | ⌘ - | Ctrl + - |
| Font reset | ⌘ 0 | Ctrl + 0 |
| Page up/down in scrollback | Shift + PgUp/PgDn | Shift + PgUp/PgDn |
| Delete just-typed characters | Backspace / Delete on a live-line selection | same |

### Teardown

```csharp
terminal.Dispose(); // stops blink + scrollbar timers, cancels
                    // in-flight search, tears down the process
                    // watcher (kqueue fd / WMI subscription).
```

`Dispose()` is idempotent; call it when the host removes the control
for good (closing a tab / pane).

## Projects

- **`Exclr8.Terminal/`** — the Avalonia control, the cell buffer, the
  parser, the renderer, input mapping, and the OS process-watch
  backends.
- **`Exclr8.Terminal.Tests/`** — xUnit test suite covering the parser,
  buffer, selection, search, resize, SGR, DEC modes, OSC, scroll
  region, character sets, wide characters, plus real-byte-stream
  replays captured from common programs.

## Build + test

```
dotnet build Exclr8.Terminal.slnx -c Debug
dotnet test Exclr8.Terminal.Tests/Exclr8.Terminal.Tests.csproj
```

Targets .NET 10 / Avalonia 11.3.

## License

Private. © Exclr8.
