# Exclr8.Terminal

A native [Avalonia](https://avaloniaui.net/) terminal control for .NET. VT500-class parser, 24-bit color, wide-character support, OSC 8 hyperlinks, scrollback with smooth pixel-scrolling, search (Cmd+F), bracketed paste, image paste via temp-file handoff, and the common DEC private modes (DECSTBM, DECAWM, DECOM, DECSCNM, IRM, LNM…).

The parser is ported from [xterm.js](https://github.com/xtermjs/xterm.js). Built to drive a real PTY through any provider; the consumer is responsible for wiring `TerminalControl.Input` to the PTY writer and PTY output bytes into `TerminalControl.Write`.

## Projects

- **`Exclr8.Terminal/`** — the Avalonia control and its buffer/parser/renderer.
- **`Exclr8.Terminal.Tests/`** — xUnit test suite (214 tests covering parser, buffer, selection, search, resize, SGR, DEC modes, OSC, scroll region, charsets, wide chars, and more).

## Build

```
dotnet build Exclr8.Terminal.slnx -c Debug
dotnet test Exclr8.Terminal.Tests/Exclr8.Terminal.Tests.csproj
```

Targets .NET 10 / Avalonia 11.3.

## License

Private. © Exclr8.
