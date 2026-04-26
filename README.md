# Exclr8.Terminal

A native Avalonia terminal control for .NET. Drop it into a view, feed
it the bytes your process produces on one side, and forward the bytes
it wants written back on the other. You get a fully-featured terminal —
parser, renderer, selection, search, scrollback, the works — with no
process-spawning or PTY plumbing baked in.

Targets .NET 10 / Avalonia 11.3.

## What it does

- **VT500-class escape-sequence parser** — ESC / CSI / OSC / DCS / APC
  / SOS / PM, 8-bit C1 sequence starts, UTF-8 assembly, sub-parameter
  parsing for SGR 4:N (curly underline) and SGR 38/48/58 colon-form
  RGB, and a full Paul Williams state diagram.
- **Two-screen model with reflow on resize** — primary + alternate
  screens, scrollback ring on the primary, resize-time line rejoin /
  re-split that follows DECAWM wrap flags through scrollback into the
  live screen.
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
  schemes.
- **Shell integration** — OSC 7 working directory, OSC 133 semantic
  prompts (PromptStart / PromptEnd / CommandStart / CommandEnd with
  exit code), OSC 9 ; 4 progress reports.
- **Dynamic palette** — OSC 4 / 10 / 11 / 12 mutations propagate to
  the renderer; shell-set defaults override the host theme;
  `PaletteChanged` fires for repaint.
- **Scrollback** with pixel-smooth wheel / trackpad scrolling and an
  auto-hiding scrollbar you can grab.
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
  Ctrl+Shift+letter / Shift+Enter / Shift+Tab.
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
  settles.
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
- **Recovery escape hatches** — `ClearActiveHyperlink()` / `SoftReset()`
  / `Reset()` for stuck OSC 8 underlines, stuck SGR pen, and full
  RIS-equivalent reset.
- **Proper disposal** — timers, watchers, in-flight search, write
  queue, and buffer event subscriptions all shut down cleanly when
  the control is removed.

## Quick start

```csharp
var terminal = new TerminalControl();
// Host it in your layout wherever you want a terminal pane.

terminal.Write(bytesFromProvider);          // PTY → terminal
terminal.Input  += (_, p) => Send(p);       // user → PTY
terminal.Output += (_, p) => Send(p);       // DSR/DA replies → PTY
terminal.Resized += (_, s) => Resize(s.Cols, s.Rows);
```

That's the minimum. Everything else is optional.

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
| `Paste(string)` | Honours bracketed-paste mode. Refuses NUL-bearing payloads. |
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
| `LineHeight` (via theme) / `CursorBlinkIntervalMs` (0 = no blink) | Visual tuning. |

### Behaviour

| Member | Purpose |
|---|---|
| `ScrollbackLimit` | Lines retained on the primary screen. |
| `ScrollSensitivity` | Pixels per wheel notch (default 40 ≈ 3 lines). |
| `WordSeparators` | Characters that bound double-click word selection. |

### Recovery

| Member | Purpose |
|---|---|
| `ClearActiveHyperlink()` | Force-clear a stuck OSC 8 link id. |
| `SoftReset()` | DECSTR — clears SGR pen, cursor visibility, scroll region, charset slots. |
| `Reset()` | RIS — clears both screens, scrollback, all DEC modes, palette overrides, OSC 8 / title state. |

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
| `SoftResetTerminal()` / `ResetTerminal()` / `ClearActiveHyperlink()` | Recovery primitives. |
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
in active use. The full surface, with citations:

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
  never straddle a wrap boundary.

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
| Clear scrollback | ⌘ K | Ctrl + Shift + K |
| Font bigger | ⌘ + | Ctrl + + |
| Font smaller | ⌘ - | Ctrl + - |
| Font reset | ⌘ 0 | Ctrl + 0 |
| Page up/down in scrollback | Shift + PgUp/PgDn | Shift + PgUp/PgDn |
| Delete just-typed characters | Backspace / Delete on a live-line selection | same |

## Teardown

```csharp
terminal.Dispose();
```

Stops blink + scrollbar + sync-output + resize-debounce timers,
cancels in-flight search, drains and discards the pending write
queue, tears down the process-tree watcher (kqueue fd / WMI
subscription), detaches buffer event handlers. Idempotent. Re-using
a disposed instance is not supported.

## Projects

- **`Exclr8.Terminal/`** — the Avalonia control, the cell buffer,
  the parser, the renderer, input mapping, link providers, marker /
  decoration infrastructure, and the OS process-watch backends.
- **`Exclr8.Terminal.Tests/`** — xUnit test suite (350+ tests)
  covering the parser, buffer, selection, search, resize + reflow,
  SGR, DEC modes, OSC, DCS, scroll region, character sets, wide
  characters, ligatures, dynamic palette, link providers, lifecycle
  events, recovery primitives, plus real-byte-stream replays
  captured from common programs.

## Build + test

```
dotnet build Exclr8.Terminal.slnx -c Debug
dotnet test  Exclr8.Terminal.Tests/Exclr8.Terminal.Tests.csproj
```

Targets .NET 10 / Avalonia 11.3.

## License

Private. © Exclr8.
