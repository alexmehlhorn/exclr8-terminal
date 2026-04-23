# Bugs found while writing workload tests

These were surfaced by the new `Workload*Tests`. Each is reproduced by
a corresponding `[Fact(Skip = "…")]` in the test project so that when
the fix lands the skip can be removed and the test kept as the
regression sentinel.

## 1. Wide glyph overwriting a narrow+wide pair leaves an orphan continuation cell

**Where:** `Exclr8.Terminal/Buffer/TerminalBuffer.cs`, `Print(int rune)`
(the wide-cell overwrite cleanup, roughly lines 570-597).

**Symptom:** After the following byte sequence on a fresh buffer
(20 cols, 8 rows):

```
? 🤜 ESC 8 🏃
```

…cell (0,2) is left with `CellFlags2.IsContinuation` set even though
cell (0,1) (its would-be wide left half) has been overwritten and is
now an `IsContinuation` cell itself — not an `IsWide` cell. The
renderer / selection layer will treat (0,2) as a zero-width phantom
that can't be selected.

**Minimal deterministic repro (11 bytes):**

```
3F F0 9F A4 9C 1B 38 F0 9F 8F 83
 ?  🤜 (U+1F91C)  ESC '8'  🏃 (U+1F3C3)
```

As a test sequence:

```csharp
var buf = new TerminalBuffer(20, 8);
buf.Write(Encoding.UTF8.GetBytes("?🤜"));       // ?  @ (0,0)   🤜 @ (0,1)+(0,2)
buf.Write(new byte[] { 0x1B, (byte)'8' });      // DECRC → cursor back to (0,0)
buf.Write(Encoding.UTF8.GetBytes("🏃"));        // 🏃 overwrites (0,0)+(0,1)
// Now (0,0)=🏃 IsWide, (0,1)=IsContinuation (new), (0,2)=IsContinuation (orphaned)
```

**Root cause:** `Print()` cleans the cell at `CursorCol + 1` only when
the *existing* cell at `CursorCol` is flagged `IsWide`. When a wide
glyph overwrites the *right half* of a previous wide glyph (i.e. the
new left half lands on an old `IsWide` cell one slot to the right),
the old glyph's continuation two slots to the right is missed.

Suggested fix: in the wide-write branch, also clear the
`IsContinuation` flag at `CursorCol + 2` if the cell at `CursorCol + 1`
was `IsWide` before being overwritten with the new continuation.

**Regression test (skipped):**
`WorkloadBugRepros.OverwriteWideWithWide_DoesNotLeaveOrphanContinuation`
in `Exclr8.Terminal.Tests/WorkloadTortureTests.cs`.
