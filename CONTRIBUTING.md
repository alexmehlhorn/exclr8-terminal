# Contributing to Exclr8.Terminal

Thanks for your interest. This document is short on purpose: most of
what matters is "open an issue to discuss before sinking time into a
big PR" and "tests pass and the diff is focused."

## Reporting bugs

Open a GitHub issue with:

- **What you observed** — exact symptom, not just "it doesn't work."
- **What you expected.**
- **Reproduction.** Ideally a minimal byte stream you can reproduce
  by feeding `terminal.Write(...)`. If a TUI is involved, the
  protocol trace is more useful than a description:

  ```csharp
  TerminalLog.EnableProtocolTrace = true;
  TerminalLog.Trace = msg =>
      System.IO.File.AppendAllText("/tmp/term-trace.log", msg + "\n");
  ```

  Reproduce, attach the trace.
- **Environment** — OS, Avalonia version, .NET version, font.

Rendering bugs benefit from a screenshot. Cell-state bugs benefit
from `terminal.Buffer.Serialize()` output.

## Suggesting features

Open an issue first. The library has an opinionated scope:

- **In scope**: anything VT compatibility, parser correctness,
  reflow / resize, performance under streaming workloads, shell
  integration (OSC), cross-platform consistency, accessibility
  for the existing surface.
- **Out of scope (for now)**: inline images (sixel / iTerm IIP /
  kitty graphics) — these belong in optional addons that plug into
  `RegisterDcsHandler` / `RegisterOscHandler`. Full kitty keyboard
  protocol — opt-in addon territory. ReGIS / Tektronix — too
  niche.

If you want to ship an addon (image protocol, custom keyboard
protocol), keep it in your own repo and depend on this package; the
parser-extensibility hooks are stable public API specifically so
addons don't need to live in-tree.

## Pull requests

1. **Open an issue first** for non-trivial changes. Saves both of us
   from writing a PR that gets rejected on scope.
2. **One concern per PR.** Bug fix or feature, not both, not "and
   while I was in there I also..."
3. **Tests required.** New behaviour gets a regression test. Bug
   fixes get a test that fails before the fix and passes after. The
   `Exclr8.Terminal.Tests/` project is xUnit; pattern your tests on
   the existing files.
4. **Keep the diff readable.** No drive-by reformatting of unrelated
   files. No renaming things that don't need renaming.
5. **Public API changes** — surface them in the PR description so
   they get an explicit review. Breaking changes need a MAJOR
   version bump (we follow SemVer).
6. **Comment style** — comments explain *why*, not *what*. The
   existing files are the model: short paragraphs, weighted toward
   non-obvious invariants and reasoning.

## Building and testing

```sh
dotnet build Exclr8.Terminal.slnx -c Debug
dotnet test  Exclr8.Terminal.Tests/Exclr8.Terminal.Tests.csproj
```

The test suite runs in under a second. Please make sure it passes
before submitting.

## Code style

- C# — match the existing files. We don't ship an `.editorconfig` to
  enforce; the patterns in the existing source are the contract.
- File-scoped namespaces, expression-bodied members where they fit,
  XML doc comments on public API.
- No `async void` outside event handlers.
- Allocations on the parser / Print / per-frame render paths get
  scrutinised. If you're touching one of those, mention the
  allocation profile in the PR.

## Reporting security issues

Don't open a public issue for vulnerabilities. Email the maintainer
(see the package metadata) with details and we'll coordinate
disclosure.

## License

By submitting a contribution, you agree that your changes are
licensed under the MIT License, the same license as the project
(see [`LICENSE`](LICENSE)).
