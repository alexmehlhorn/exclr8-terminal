# Exclr8.Terminal

[![NuGet](https://img.shields.io/nuget/v/Exclr8.Terminal.svg)](https://www.nuget.org/packages/Exclr8.Terminal)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)
[![Platforms](https://img.shields.io/badge/platforms-macOS%20%7C%20Windows%20%7C%20Linux-blue)](https://github.com/alexmehlhorn/exclr8-terminal)

A native Avalonia terminal control for .NET. Drop it into a view, feed
it the bytes your process produces on one side, and forward the bytes
it wants written back on the other. You get a fully-featured terminal —
parser, renderer, selection, search, scrollback, the works — with no
process-spawning or PTY plumbing baked in.

Targets **.NET 10** and **Avalonia 12.0**.

## Cross-platform

Runs on **macOS**, **Windows**, and **Linux** — anywhere Avalonia
runs. Same source, same package, identical API surface. Pixel
rendering goes through Avalonia's Skia backend (so you get the same
glyph shaping, ligature handling, and font metrics across platforms).

Platform-specific code is contained in two clearly-isolated places:

- **Process-tree watching** — pluggable `IProcessChildWatcher` with
  three backends: `KQueueChildWatcher` (macOS / *BSD), `WmiChildWatcher`
  (Windows, gated by `[SupportedOSPlatform("windows")]` so non-Windows
  builds link cleanly), `NoopChildWatcher` (Linux fallback). The
  factory picks the right one at runtime.
- **Clipboard image paste** — uses Avalonia's cross-platform
  `IClipboard.TryGetDataAsync`. When Avalonia's typed bitmap
  extractor misses (which on Windows includes Snipping Tool, modern
  browsers, and most paint apps), a raw-bytes fallback walks the
  clipboard items × formats matrix, pulls bytes for any known image
  identifier (`PNG` / `image/png` / `public.png`, JPEG / TIFF / BMP
  variants, `CF_DIB` / `CF_DIBV5`), magic-byte-sniffs to confirm,
  prepends a synthetic `BITMAPFILEHEADER` for header-less DIB
  payloads, and writes a temp file whose path is pasted. Combined,
  the typed and raw paths cover screenshot-to-clipboard from every
  source we've tested across the three platforms.

Everything else — parser, buffer, renderer, input, search, selection,
reflow, ligatures, OSC handlers — is platform-agnostic .NET code.

## Install

```sh
dotnet add package Exclr8.Terminal
```

Or in your `.csproj`:

```xml
<PackageReference Include="Exclr8.Terminal" Version="1.0.7" />
```

## Status

Production-ready. ~370 unit tests covering the parser, buffer, search,
selection, resize + reflow, SGR, DEC modes, OSC, DCS, character sets,
wide characters, ligatures, dynamic palette, link providers, lifecycle
events, and recovery primitives. Used in shipped products for daily
work with shells, vim, helix, claude-code, codex, tmux, and friends.

The library handles the **terminal-emulator** half of the problem.
You're responsible for the **PTY half** (spawning the shell, wiring
stdin/stdout/stderr, sending SIGWINCH on resize). On macOS / Linux a
small `pty.h` wrapper does the job; on Windows ConPTY is the standard
path. The control is intentionally agnostic — it works equally well
with a local PTY, an SSH channel, an in-memory replay stream, or a
recorded session.

> **Want a working local PTY out of the box?** Add the optional
> sister package
> [`Exclr8.Terminal.Pty`](https://www.nuget.org/packages/Exclr8.Terminal.Pty) —
> wires [Porta.Pty](https://www.nuget.org/packages/Porta.Pty) to a
> `TerminalControl` in one `await adapter.StartAsync(options)` call
> (read loop, writer lock, resize, dispose ordering, all included).
> The core stays PTY-agnostic; the sister package is the spawn-and-go
> layer for hosts that want a local shell. See
> [`samples/SimpleTerminal`](samples/SimpleTerminal) for a complete
> ~70-line example.

## What it does

- **VT500-class escape-sequence parser** — ESC / CSI / OSC / DCS / APC
  / SOS / PM, 8-bit C1 sequence starts, UTF-8 assembly, sub-parameter
  parsing for SGR 4:N (curly underline) and SGR 38/48/58 colon-form
  RGB, and a full Paul Williams state diagram.
- **Two-screen model with reflow on resize** — primary + alternate
  screens, scrollback ring on the primary, resize-time line rejoin /
  re-split that follows DECAWM wrap flags through scrollback into the
  live screen. Resize preserves history.
- **Full SGR styling** — 24-bit RGB, 256-palette indices, bold,
  italic, underline (single / double / curly / dotted / dashed),
  strikethrough, inverse, dim, blink, SGR 58 underline colour.
- **Programming-font ligatures** (opt-in) — `liga` / `clig` / `calt`
  via Avalonia's `FormattedText.SetFontFeatures`, so Fira Code /
  JetBrains Mono / Cascadia Code substitutions like `==`, `->`, `!=`
  light up automatically.
- **Unicode + wide characters** — CJK, fullwidth punctuation, emoji,
  astral-plane runes; VS16 retro-widens narrow base characters into
  emoji presentation; combining marks and bidi format codepoints
  attach to the preceding cell instead of advancing.
- **OSC 8 hyperlinks** + **plain-URL link providers** — `https?://`
  matching out of the box via `WebLinkProvider`; hosts can register
  their own `ILinkProvider` for issue numbers, file paths, vendor
  schemes. `LinkActivationPolicy` defaults to `http://` / `https://`
  to keep `javascript:` and `file://` from arbitrary OSC 8 emitters
  out of your host.
- **Shell integration** — OSC 7 working directory, OSC 133 semantic
  prompts (PromptStart / PromptEnd / CommandStart / CommandEnd with
  exit code), OSC 9;4 taskbar / dock-badge progress.
- **Dynamic palette** — OSC 4 / 10 / 11 / 12 mutations propagate to
  the renderer; shell-set defaults override the host theme;
  `PaletteChanged` fires for repaint.
- **Scrollback** with pixel-smooth wheel / trackpad scrolling, an
  auto-hiding scrollbar you can grab, and **drag-select auto-scroll**
  when the pointer leaves the viewport.
- **Find-in-buffer** — case-insensitive (default), case-sensitive,
  whole-word, regex; debounced; runs off the UI thread; cancellable;
  navigate match-by-match.
- **Selection** — character drag, word on double-click, line on
  triple-click; absolute-anchored so scrolling doesn't smear the
  highlight; programmatic `Select(...)` over absolute coordinates.
- **Clipboard** — copy selection, paste text, paste an image from the
  clipboard (spilled to a temp file whose path is pasted). Bracketed
  paste honoured when the shell asks for it. OSC 52 host-gated.
- **Cursor styles** via DECSCUSR (block / underline / bar, blinking
  or steady); blink interval host-configurable; visibility via
  DECTCEM.
- **Mouse reporting** — X10 (DECSET 9), VT200 (1000), button-event
  (1002), any-event (1003); SGR (1006) and SGR-pixel (1016) encodings.
- **Keyboard** — DECCKM application cursor keys, DECKPAM application
  keypad, modifyOtherKeys level 2 (`CSI > 4 ; 2 m`) for unambiguous
  Ctrl+Shift+letter / Shift+Enter / Shift+Tab. AltGr-correct on
  Windows / Linux (Ctrl+Alt-as-AltGr text isn't ESC-prefixed).
- **Markers + decorations** — persistent line references that survive
  scroll-into-scrollback (`RegisterMarker`), with overlay
  decorations anchored to them (`RegisterDecoration`).
- **Parser extensibility** — `RegisterCsiHandler`,
  `RegisterOscHandler`, `RegisterEscHandler`, `RegisterDcsHandler`
  on `TerminalBuffer` so addons (sixel, kitty graphics, vendor
  sequences) can plug in without forking.
- **Serialize** — VT-replayable dump of scrollback + live screen +
  cursor + SGR transitions for session save / snapshot tests.
- **Synchronized output (DECSET 2026)** — TUIs holding the mode get
  flicker-free atomic frames; a 150 ms safety timer prevents a
  misbehaving emitter from freezing the screen.
- **Write coalescing** — multi-thread-safe `Write(...)` queues into
  a single dispatcher pass; bursts of small chunks collapse to one
  drain; configurable drop policy + queue cap for untrusted
  producers.
- **Resize debounce** — drag-resize gestures and reparent storms
  collapse into one buffer resize + `Resized` event after the burst
  settles. A small pixel deadband around each cell-grid integer
  boundary keeps host-side layout micro-jitter (focus-ring border
  thickness flips, scrollbar fade, font-hinting nudges of 1–2 px)
  from spuriously flipping the grid by one cell — important on
  Windows where any spurious resize forwards to ConPTY and reframes
  the screen.
- **Top-level focus tracking** — DECSET 1004 `\e[I` / `\e[O` fire on
  OS-window activation, not on internal pane / tab switches; matches
  iTerm2 / Terminal.app / WezTerm behaviour and prevents TUI
  redraw-storms on tab activation.
- **Process-tree watching** — optional OS-level notifications
  (kqueue on macOS, WMI on Windows) when the shell forks or a
  descendant exits. Useful for "running process" badges and session
  tagging.
- **Theming** — foreground, background, cursor, and ANSI palette
  overrides; partial palettes safely fall through to defaults.
- **Font zoom** — Cmd/Ctrl `+`, `-`, `0` to bump, shrink, reset.
- **Diagnostic tracing** — opt-in protocol-trace channel surfaces
  every unhandled CSI / OSC / DCS / DEC mode for compatibility
  debugging without polluting the silent run.
- **Recovery primitives** — `ClearActiveHyperlink()` for stuck OSC 8
  state, `SoftReset()` for stuck SGR pen, `Reset()` for full
  RIS-equivalent reset, `ClearScreenAndScrollback()` for Cmd+K-style
  cleanup that preserves the user's prompt block.
- **Proper disposal** — timers, watchers, in-flight search, write
  queue, and buffer event subscriptions all shut down cleanly when
  the control is removed.

## What it doesn't do

So you know what to wire externally:

- **Spawning shells / managing PTYs.** Bring your own `pty.h`/`ConPTY`
  layer; the control just wants bytes in (`Write(...)`) and bytes out
  (`Input` / `Output` events).
- **Inline images** (sixel, iTerm2 IIP, kitty graphics). DCS handlers
  exist (`RegisterDcsHandler`) so an image addon can plug in, but no
  decoder ships in-box. Building one is a real project — pixel
  storage, atlas management, GPU upload — and out of scope for the
  core control.
- **Kitty keyboard protocol** beyond modifyOtherKeys level 2.
  Modern editors (helix, neovim) work great with what's there;
  full kitty protocol with progressive enhancement and per-buffer
  flag stacks isn't implemented.
- **Sixel / ReGIS / Tektronix.**
- **A11y / screen-reader** integration. Avalonia's `AutomationPeer`
  surface isn't wired up.

## Quick start

Minimal Avalonia view with a working terminal panel — wire your PTY
adapter into the four events:

```csharp
using Avalonia.Controls;
using Exclr8.Terminal;

public class TerminalView : UserControl
{
    public TerminalView()
    {
        var terminal = new TerminalControl();
        Content = terminal;

        // PTY → terminal: bytes the shell produced go in.
        // Call from anywhere; the control coalesces to the UI thread.
        myPty.OnStdout(bytes => terminal.Write(bytes));

        // user → PTY: bytes the user typed/pasted go out.
        terminal.Input += (_, payload) => myPty.Write(payload.Span);

        // terminal-protocol replies (DSR, DA, DECRQM, OSC queries).
        terminal.Output += (_, payload) => myPty.Write(payload.Span);

        // Cell grid changed — propagate to the PTY (SIGWINCH).
        terminal.Resized += (_, size) => myPty.Resize(size.Cols, size.Rows);

        // Click handler for OSC 8 / detected URLs.
        terminal.HyperlinkClicked += (_, url) => OpenInBrowser(url);
    }
}
```

That's the minimum. Everything else is optional.

### Recommended host setup

For a clean Claude Code / vim / Codex experience:

```csharp
// Issue Reset() before connecting a freshly-spawned PTY so dimension-
// detection races during the app's startup don't leave stacked
// partial renders in scrollback.
terminal.PrepareForNewSession();

// Restrict link activation to web schemes by default — OSC 8 can
// emit any URL.
terminal.LinkActivationPolicy = url =>
    url.StartsWith("http://",  StringComparison.OrdinalIgnoreCase) ||
    url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

// Detect plain URLs in shell output, not just OSC 8.
terminal.RegisterLinkProvider(new WebLinkProvider());

// Programming-font ligatures if you ship Fira Code / JetBrains Mono.
terminal.EnableLigatures = true;
```

### Host integration notes

A few non-obvious gotchas if you're embedding the control inside a
multi-pane layout, especially with `cmd.exe` on Windows:

#### Don't change layout dimensions on focus

If your host draws a focus ring around terminal cells, **do not flip
`BorderThickness` (or any padding / margin) on focus**. A 1–2 px
delta on focus change moves the inner area, the control sees a
`Bounds` change, integer truncation in `RecomputeGrid` may flip the
column or row count by one, and a `Resized` event fires. On Windows
that propagates through `ResizePseudoConsole` and ConPTY reframes the
screen — see the next note for why that's destructive.

Use a constant-thickness border with a colour swap instead:

```csharp
// Always Thickness(2). Brush distinguishes focused / resting.
Frame.BorderThickness = new Thickness(2);
Frame.BorderBrush = focused ? FocusBrush : RestingBrush;
```

The control ships a 3 px deadband around single-cell boundary
crossings as defence in depth, so most jitter sources are absorbed —
but the cleanest fix is to not jitter the layout in the first place.

#### ConPTY + cmd.exe loses blank rows on reframe

On Windows hosting `cmd.exe` through ConPTY, any `ResizePseudoConsole`
call causes ConPTY to reframe `cmd.exe`'s screen buffer. `cmd.exe`'s
`echo.` (and any other "advance cursor without writing") leaves rows
in their default-padding state — indistinguishable from rows the
cursor never visited — and ConPTY's reframe collapses them away.
The visible symptom is "blank lines disappear on resize". This is
fundamental to how the Win32 console screen buffer represents
unwritten cells; the control can't recover the rows once ConPTY
emits the new frame. The only mitigation is **don't trigger
spurious reframes** — see the focus-ring note above. macOS / Linux
PTYs are byte streams and aren't affected.

#### Window-level vs control-level focus reporting

DECSET 1004 focus events (`\e[I` / `\e[O`) default to firing on
**OS-window** activation, not on internal pane / tab switches.
This matches iTerm2 / Terminal.app / WezTerm and avoids storms of
focus-out / focus-in to every shell when the user clicks between
panes inside one window. Set `terminal.FocusEventSource =
FocusEventSource.Control` if you specifically want per-pane
reporting.

#### Paste interception

The control owns `Cmd/Ctrl+V` end-to-end via
`PasteFromClipboardAsync()` — clipboard read, image-bytes fallback,
bracketed paste framing, all of it. **Don't intercept paste at the
host level and feed text in via `terminal.Paste(text)` yourself**:
that path skips the image-paste handling, and an image-only
clipboard becomes a silent no-op. Either let the control handle
paste natively, or call `PasteFromClipboardAsync()` from your own
keybinding / menu so all the right work still happens.

## Public API surface

`TerminalControl` is the host-facing entry point. All members below
live on it directly; deeper functionality (parser hooks, markers,
serialize, dynamic palette) is reachable via `terminal.Buffer`.

### Wiring

| Member | Purpose |
|---|---|
| `Write(ReadOnlySpan<byte>)` / `Write(byte[])` | Feed bytes from the PTY / SSH channel. Thread-safe; coalesced. |
| `Input` | Bytes the user typed, ready for the PTY. |
| `Output` | DSR / DA / DECRQM / OSC-query replies the terminal wants forwarded to the PTY. |
| `Resized` | Cell grid dimensions changed (debounced). |
| `Buffer` | Underlying `TerminalBuffer` for advanced extensibility. |
| `InputEvents` | Local observer stream over user input — `LineCommitted`, origin tagging (Typed / Pasted / Programmatic). |

### Write queue / backpressure

| Member | Purpose |
|---|---|
| `QueuedBytes` | Bytes waiting in the pending-write queue. |
| `DroppedBytes` | Bytes discarded by the drop policy since construction. |
| `WriteDropPolicy` | `None` (default, unlimited) or `OldestFirst`. |
| `WriteQueueMaxBytes` | Cap when the policy is `OldestFirst`. |

### Hyperlinks + link providers

| Member | Purpose |
|---|---|
| `HyperlinkClicked` | OSC 8 link OR provider-detected URL was clicked and passed the policy. |
| `LinkBlocked` | Click was rejected by `LinkActivationPolicy`. |
| `LinkActivationPolicy` | `Func<string, bool>`. Default allows `http://` / `https://`. |
| `RegisterLinkProvider(ILinkProvider)` | Returns `IDisposable`; built-in `WebLinkProvider` matches plain URLs. |
| `LinkProviders` | Read-only list of currently registered providers. |
| `ShowHyperlinkUnderline` | Toggle the 1-px underline beneath OSC 8 cells. |

### Shell integration

| Member | Purpose |
|---|---|
| `Bell` | `BEL` (0x07) received. Host decides sound / flash / notification. |
| `TitleChanged` / `IconNameChanged` | OSC 0 / 1 / 2. |
| `WorkingDirectoryChanged` / `WorkingDirectory` | OSC 7 — shell-announced CWD. |
| `SemanticPrompt` | OSC 133 — PromptStart / PromptEnd / CommandStart / CommandEnd + exit code. |
| `ProgressChanged` | OSC 9 ; 4 — taskbar / dock-badge progress (state + 0..100 percent). |

### Lifecycle

| Member | Purpose |
|---|---|
| `CursorMoved` | Cursor row/col changed during a Write (debounced to once per write). |
| `ScrollChanged` | Scroll offset changed. |
| `SelectionChanged` | Selection set, extended, or cleared. |
| `Resized` | Grid dimensions changed (debounced). |

### Selection

| Member | Purpose |
|---|---|
| `HasSelection` | True when anything is selected. |
| `GetSelectionText()` | Plain text of the current selection. |
| `GetSelectionPosition()` | `(StartRow, StartCol, EndRow, EndCol)` in absolute coords, or null. |
| `Select(startRow, startCol, endRow, endCol)` | Programmatic selection over absolute rows. |
| `SelectLineByAbs(absRow)` | Select a whole absolute row. |
| `SelectAll()` | Scrollback + live screen. |
| `ClearSelection()` | Drop active selection. |
| `CopySelectionAsync()` | Copy current selection to OS clipboard. |
| `PasteFromClipboardAsync()` | Paste OS clipboard (text or image-bytes-as-temp-file). |

### Find

| Member | Purpose |
|---|---|
| `FindRequested` | User pressed Cmd/Ctrl+Shift+F — host shows its find UI. |
| `Find(needle, SearchOptions?)` | Async, debounced; supports CaseSensitive, WholeWord, Regex. |
| `FindNext()` / `FindPrev()` | Walk results; wraps. |
| `CloseFind()` | Cancel in-flight search and clear matches. |
| `MatchCount` / `CurrentMatch` | UI-friendly counters (`current` is 1-based). |

### Clipboard

| Member | Purpose |
|---|---|
| `Paste(string)` | Honours bracketed-paste mode; scrubs any embedded `ESC [ 201 ~` close markers in the body so a malicious or accidental paste can't terminate paste mode early. Forwards bytes verbatim — NULs and other control bytes pass through, matching iTerm2 / Terminal.app. |
| `PasteMaxBytes` | Hard cap on paste payload size in bytes. Default 50 MB. Settable; set to `int.MaxValue` for no effective cap. |
| `PasteRejected` | `EventHandler<long>` — fires with the rejected size in bytes when a paste exceeded `PasteMaxBytes`. Subscribe to surface a "paste too large" toast; without a subscriber the cap is silent. |
| `PasteChunkSize` | Bytes per chunk for split delivery into the `Input` event. **Default 0 — chunking off**, whole paste fires as one event. Opt-in only: enabling chunking requires the host to serialise its `Input`-event writer (queue / lock / `SemaphoreSlim`), otherwise concurrent `WriteAsync` calls race on the underlying handle. |
| `PasteChunkDelayMs` | Optional inter-chunk delay (ms) when chunking is on. 0 yields without sleeping; 1–10 ms helps if the consumer is genuinely slow. |
| `AllowClipboardAccess` | Gate for OSC 52 (off by default — remote can scrape clipboard otherwise). |
| `ClipboardRequested` | OSC 52 set request — fires only when allowed. |
| `PasteImageDirectoryName` | Static; sub-dir under temp for spilled clipboard images. |

### Appearance

| Member | Purpose |
|---|---|
| `FontFamily` / `FontSize` / `DefaultFontSize` | Family + size; reset via Cmd/Ctrl+0. |
| `AdjustFontSize(direction)` / `ResetFontSize()` | Cmd/Ctrl+= / -. |
| `EnableLigatures` | OpenType liga/clig/calt for programming fonts. |
| `ColorScheme` | `TerminalTheme` (foreground / background / cursor / 16-entry ANSI). |
| `CursorBlinkIntervalMs` | Cursor blink period (0 = no blink). |

### Behaviour

| Member | Purpose |
|---|---|
| `ScrollbackLimit` | Lines retained on the primary screen. |
| `ScrollSensitivity` | Pixels per wheel notch (default 40 ≈ 3 lines). |
| `WordSeparators` | Characters that bound double-click word selection. |
| `FocusEventSource` | `TopLevel` (default — DECSET 1004 fires on OS-window focus) or `Control` (per-pane). |

### Recovery

| Member | Purpose |
|---|---|
| `ClearActiveHyperlink()` | Force-clear a stuck OSC 8 link id. |
| `ClearScreenAndScrollback()` | Cmd+K — wipes screen + scrollback, preserves the prompt block when OSC 133 is wired up. |
| `SoftReset()` | DECSTR — clears SGR pen, cursor visibility, scroll region, charset slots. |
| `Reset()` / `PrepareForNewSession()` | RIS — clears both screens, scrollback, all DEC modes, palette overrides, OSC 8 / title state. |

### Process-tree watching

| Member | Purpose |
|---|---|
| `RootProcessId` | Shell pid the watcher hangs off. |
| `ProcessTreeChanged` | Fires Created / Exited under the root subtree. |

## Deeper API on `TerminalBuffer`

Reachable via `terminal.Buffer`. Hosts that build advanced UX use these.

### Parser hooks

| Member | Purpose |
|---|---|
| `RegisterCsiHandler(final, prefix, CsiHandler)` | Intercept a CSI dispatch; return `true` to claim it. |
| `RegisterOscHandler(id, OscHandler)` | Intercept an OSC by numeric id (e.g. 1337 for iTerm IIP). |
| `RegisterEscHandler(final, intermediates, EscHandler)` | Intercept an ESC dispatch. |
| `RegisterDcsHandler(final, intermediates, DcsHandler)` | Intercept a DCS — sixel, DECRQSS, vendor extensions. |

### Markers + decorations

| Member | Purpose |
|---|---|
| `RegisterMarker(cursorYOffset)` | Anchor a `TerminalMarker` to a content line. Survives scroll-into-scrollback. |
| `RegisterDecoration(DecorationOptions)` | Visual overlay anchored to a marker; bottom or top layer. |
| `Decorations` | Read-only list of live decorations. |
| `ScrollbackEvictions` | Monotonic counter of dropped scrollback lines (markers use this internally). |

### Serialize + replay

| Member | Purpose |
|---|---|
| `Serialize()` | VT-replayable string capturing scrollback + live screen + cursor + SGR transitions. Feed back through `Write(...)` to restore. |
| `SnapshotRows()` | Cell-array snapshot for off-thread search / analysis. |
| `ScanMatches(rows, needle, SearchOptions, ct)` | Static — scan a snapshot; safe off-thread. |

### Dynamic palette + default colours

| Member | Purpose |
|---|---|
| `DefaultForegroundRgb` / `DefaultBackgroundRgb` / `DefaultCursorRgb` | OSC 10 / 11 / 12 reported values. |
| `DefaultForegroundExplicit` / `DefaultBackgroundExplicit` / `DefaultCursorExplicit` | True once the shell has explicitly set the corresponding default. |
| `TryGetDynamicPaletteColor(idx, out rgb)` | OSC 4 override for palette index `idx`. |
| `PaletteChanged` | OSC 4 / 10 / 11 / 12 mutation. |

### Direct buffer state

| Property | Purpose |
|---|---|
| `Cols` / `Rows` | Active grid size. |
| `CursorRow` / `CursorCol` / `CursorVisible` / `CursorStyle` | Cursor state. |
| `ScrollTop` / `ScrollBottom` | DECSTBM region. |
| `IsAltScreen` | True when 1049/1047/47 is active. |
| `BracketedPaste` / `ApplicationCursorKeys` / `ApplicationKeypad` / `MouseMode` / `MouseEncoding` / `FocusEvents` / `AutoWrap` / `OriginMode` / `ReverseVideo` / `InsertMode` / `LineFeedNewLine` / `ReverseWraparound` / `SynchronizedOutput` / `ModifyOtherKeys` | Live DEC / ANSI mode flags. |
| `ScrollOffset` / `PixelScrollOffset` | Scrollback viewport. |
| `Selection` / `SearchNeedle` / `SearchMatches` / `CurrentMatchIndex` | Selection + search state. |
| `Revision` | Bumps on every state change — host can use for change detection. |

### Buffer methods

| Method | Purpose |
|---|---|
| `Write(bytes)` | Feed bytes through the parser. |
| `Resize(cols, rows)` | Resize with reflow. |
| `Clear()` / `ClearScrollback()` | Live screen / scrollback wipes. |
| `SetScrollOffset(n)` / `ScrollByPixels(px, lineHeight)` / `ScrollViewUp/Down(n)` / `ResetScrollOffset()` | Scrollback navigation. |
| `Select(...)` / `SelectAll()` / `ClearSelection()` / `StartSelection` / `ExtendSelection` / `SelectWord` / `SelectLine` | Selection control. |
| `Search(needle)` / `ApplySearchResults(needle, matches)` / `NextMatch()` / `PrevMatch()` / `ClearSearch()` | Sync + async search. |
| `RegisterMarker` / `RegisterDecoration` | (above) |
| `RegisterCsiHandler` / `RegisterOscHandler` / `RegisterEscHandler` / `RegisterDcsHandler` | (above) |
| `Serialize()` | (above) |
| `SoftResetTerminal()` / `ResetTerminal()` / `ClearActiveHyperlink()` / `ClearScreenAndScrollback()` | Recovery primitives. |
| `NotifyFocus(focused)` | Drive DECSET 1004 focus reports. |
| `TryGetHyperlink(id, out url)` | Resolve an OSC 8 cell's link id. |

## Diagnostics

| Member | Purpose |
|---|---|
| `TerminalLog.Error` | Non-fatal-error sink (default: `Console.Error`). |
| `TerminalLog.Trace` | Protocol-trace sink (default: `Console.Error`). |
| `TerminalLog.EnableProtocolTrace` | Off by default. When on, every unhandled CSI / OSC / DCS / DEC mode is logged. |

## VT compatibility

Implements VT100 / VT220 / much of VT420, plus the xterm extensions
in active use. The full surface:

- **CSI**: CUU/CUD/CUF/CUB, CNL/CPL, CHA/HPA/HPR/VPA/VPR, CUP/HVP, CHT/CBT,
  ED/DECSED/EL/DECSEL, IL/DL, DCH, ICH, ECH, SU/SD, SL/SR, REP, TBC,
  IRM/LNM (SM/RM), SGR (full incl. 4:N + 38/48/58 RGB), DSR 5/6,
  DECDSR (DECXCPR + stubs), DA1/DA2/DA3, DECSTBM, DECSC/DECRC,
  DECSCUSR, DECRQM (ANSI + DEC), DECRQSS via DCS \$q, DECIC/DECDC,
  DECSCA accept (no protection enforcement), XTWINOPS report ops,
  XTMODKEYS (`CSI > 4 ; level m`), XTVERSION (`CSI > q`).
- **ESC**: DECSC/DECRC, IND/NEL/RI, HTS, DECKPAM/DECKPNM, RIS,
  SCS designators (`( )` G0/G1, US ASCII + DEC special graphics).
- **DEC private modes (DECSET / DECRST)**: 1, 5, 6, 7, 9, 25, 45,
  47, 1000, 1002, 1003, 1004, 1006, 1016, 1047, 1048, 1049, 2004,
  2026.
- **OSC**: 0, 1, 2, 4, 7, 8, 9 (incl. 9;4 progress), 10, 11, 12,
  52, 133.
- **DCS**: DECRQSS (`$q`); other DCS sequences dispatched to
  registered handlers (sixel / kitty image addons can plug in here).
- **C0 / C1**: full UTF-8 assembly, surrogate / over-long rejection,
  C1 8-bit sequence starts (CSI/OSC/DCS/SOS/PM/APC).
- **Reflow**: per-row wrap-flag tracking; resize joins wrapped runs
  into logical lines and re-splits at the new width; wide cells
  never straddle a wrap boundary; resize preserves shell history
  by routing scrolled-out rows into scrollback (alt-screen excepted).

## Extensibility model

Host the control as an `Avalonia.Controls.Control`. Subscribe to
events you care about. Plug in addons via the parser-hook APIs
without forking the buffer:

```csharp
// Custom OSC 1337 (iTerm2 inline image protocol)
using var _ = terminal.Buffer.RegisterOscHandler(1337, payload =>
{
    // decode iTerm IIP inline-image base64, render via host overlay
    return true; // we handled it — built-in path skipped
});

// Custom CSI for vendor sequence
using var _ = terminal.Buffer.RegisterCsiHandler('z', '?', (ps, intermediates) =>
{
    // ...
    return true;
});
```

## Built-in shortcuts

| Action | macOS | Windows / Linux |
|---|---|---|
| Copy selection | ⌘ C | Ctrl + Shift + C, or Ctrl + C when a selection exists |
| Paste | ⌘ V | Ctrl + V, or Ctrl + Shift + V |
| Select all | ⌘ A | Ctrl + Shift + A |
| Open find | ⌘ F | Ctrl + Shift + F |
| Clear screen + scrollback | ⌘ K | Ctrl + Shift + K |
| Font bigger | ⌘ + | Ctrl + + |
| Font smaller | ⌘ - | Ctrl + - |
| Font reset | ⌘ 0 | Ctrl + 0 |
| Page up/down in scrollback | Shift + PgUp/PgDn | Shift + PgUp/PgDn |
| Delete just-typed characters | Backspace / Delete on a live-line selection | same |

## Teardown

```csharp
terminal.Dispose();
```

Stops blink + scrollbar + sync-output + resize-debounce + drag-auto-
scroll timers, cancels in-flight search, drains and discards the
pending write queue, tears down the process-tree watcher (kqueue fd
/ WMI subscription), detaches buffer event handlers. Idempotent.
Re-using a disposed instance is not supported.

## Build + test

```sh
dotnet build Exclr8.Terminal.slnx -c Debug
dotnet test  Exclr8.Terminal.Tests/Exclr8.Terminal.Tests.csproj
```

Targets .NET 10 / Avalonia 11.3.

## Repository layout

- **`Exclr8.Terminal/`** — the Avalonia control, the cell buffer,
  the parser, the renderer, input mapping, link providers, marker /
  decoration infrastructure, and the OS process-watch backends.
- **`Exclr8.Terminal.Tests/`** — xUnit test suite covering the
  parser, buffer, selection, search, resize + reflow, SGR, DEC
  modes, OSC, DCS, scroll region, character sets, wide characters,
  ligatures, dynamic palette, link providers, lifecycle events,
  recovery primitives, plus real-byte-stream replays captured from
  common programs.

## Contributing

Issues and pull requests are welcome. Please see
[`CONTRIBUTING.md`](CONTRIBUTING.md) for the bare-minimum process
notes (branching, coding style, what counts as a good bug report,
how to run tests). The short version: open an issue to discuss
non-trivial changes before sinking time into a PR.

## Inspirations / prior art

The parser owes its shape to **xterm**'s state diagram (Paul Williams)
and **xterm.js**'s `EscapeSequenceParser`. Many specific behaviours —
reflow, OSC 133, cursor / link / selection semantics — borrow from
**iTerm2**, **WezTerm**, **kitty**, and **Windows Terminal** where
they've already worked out what users expect.

## License

Licensed under the [MIT License](LICENSE).

```
Copyright (c) 2026 Exclr8 Business Automation (Pty) Ltd

Permission is hereby granted, free of charge, to any person obtaining a
copy of this software and associated documentation files (the "Software"),
to deal in the Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, subject to the conditions in the
LICENSE file.
```

The [`LICENSE`](LICENSE) file also reproduces the upstream MIT notices for
**Avalonia** (the UI framework this control is built on) and **xterm.js**
(whose `EscapeSequenceParser` shaped the parser's structure). Both are
acknowledged in the *Inspirations / prior art* section above; this is the
formal attribution.
