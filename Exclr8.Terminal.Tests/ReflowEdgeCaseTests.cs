using System.Text;
using Exclr8.Terminal.Buffer;
using Xunit;
using static Exclr8.Terminal.Tests.TestHelpers;

namespace Exclr8.Terminal.Tests;

/// <summary>
/// Reflow correctness around the cases a recent code review flagged:
/// stale wrap flags after row reuse, alt-screen resize discarding
/// primary content, cursor remapping over wide-cell wrap boundaries
/// and empty logical lines, and selection / search invalidation on
/// resize.
/// </summary>
public class ReflowEdgeCaseTests
{
    [Fact]
    public void StaleWrapFlag_OverwriteAfterCR_DoesNotRejoinUnrelatedLine()
    {
        var buf = NewBuffer(5, 4);
        // Auto-wrap at 5 cols: "abcdefghij" → row 0 "abcde" wrap=false,
        // row 1 "fghij" wrap=true.
        buf.Feed("abcdefghij");
        // Move cursor to row 1 col 0 and overwrite. The remaining cells
        // are still "ghij" and the wrap flag is still set. After this
        // overwrite the row no longer logically continues row 0 — it's
        // an independently authored line.
        buf.Feed(CSI + "2;1H");
        buf.Feed("X");
        // Resize wider. Reflow MUST NOT rejoin "abcde" + "Xghij" into
        // one logical line — they're now unrelated.
        buf.Resize(20, 4);
        Assert.Equal("abcde", buf.RowText(0).TrimEnd());
        Assert.Equal("Xghij", buf.RowText(1).TrimEnd());
    }

    [Fact]
    public void StaleWrapFlag_EraseLineDropsContinuationFlag()
    {
        var buf = NewBuffer(5, 4);
        buf.Feed("abcdefghij");      // row 1 wrap=true
        buf.Feed(CSI + "2;1H");
        buf.Feed(CSI + "2K");        // EL mode 2 (whole line)
        buf.Feed("X");
        buf.Resize(20, 4);
        // After erase, row 1 is independent; reflow shouldn't join it.
        Assert.Equal("abcde", buf.RowText(0).TrimEnd());
        Assert.Equal("X", buf.RowText(1).TrimEnd());
    }

    [Fact]
    public void StaleWrapFlag_ClearRow_DropsContinuationFlag()
    {
        var buf = NewBuffer(5, 4);
        buf.Feed("abcdefghij");      // row 1 wrap=true
        buf.Feed(CSI + "2J");        // ED mode 2: clear all rows
        buf.Feed(CSI + "1;1H");
        buf.Feed("X");
        buf.Resize(20, 4);
        // Whole screen erased then "X" written at row 0. Reflow
        // shouldn't fabricate a multi-row logical line out of the
        // ghost wrap flags.
        Assert.Equal("X", buf.RowText(0).TrimEnd());
        Assert.Equal("", buf.RowText(1).TrimEnd());
    }

    [Fact]
    public void AltScreenResize_PreservesPrimaryBottomContent()
    {
        var buf = NewBuffer(20, 5);
        buf.ScrollbackLimit = 100;
        // Fill primary with "P0..P4", cursor ends after P4 at the
        // bottom of the live screen.
        for (int i = 0; i < 5; i++)
        {
            buf.Feed($"P{i}");
            if (i < 4) buf.Feed("\r\n");
        }
        // Enter alt-screen with cursor save (DECSET 1049). The primary
        // saved cursor records the position at the bottom of "P4".
        buf.Feed(CSI + "?1049h");
        buf.Feed("alt");
        // Resize while alt is active and rows shrink. Old code passed
        // (0,0) for the inactive primary, which would drop the BOTTOM
        // rows of primary (P3, P4) on shrink. The fix passes the saved
        // cursor (near the bottom) so the bottom is preserved and the
        // top is evicted into scrollback.
        buf.Resize(20, 3);
        // Leave alt-screen — DECSET 1049 reset.
        buf.Feed(CSI + "?1049l");
        // P3 and P4 must still be reachable.
        var visible = new StringBuilder();
        for (int r = 0; r < buf.Rows; r++) visible.AppendLine(buf.RowText(r));
        var sb = new StringBuilder();
        for (int i = 0; i < buf.ScrollbackCount; i++)
        {
            buf.SetScrollOffset(buf.ScrollbackCount);
            var row = buf.GetRowForRender(i);
            if (row == null) continue;
            foreach (var c in row)
            {
                if ((c.Flags2 & CellFlags2.IsContinuation) != 0) continue;
                sb.Append(c.Rune == 0 ? ' ' : char.ConvertFromUtf32(c.Rune));
            }
            sb.AppendLine();
        }
        var combined = sb.ToString() + visible.ToString();
        Assert.Contains("P3", combined);
        Assert.Contains("P4", combined);
    }

    [Fact]
    public void Resize_ClearsSelectionAndSearch()
    {
        var buf = NewBuffer(20, 5);
        buf.Feed("hello world");
        buf.SelectAll();
        Assert.NotNull(buf.Selection);
        buf.Search("hello");
        Assert.NotEmpty(buf.SearchMatches);
        buf.Resize(40, 5);
        // Reflow shifted absolute row indices. Selection and search
        // matches are absolute-anchored and would now point at stale
        // rows — clear them.
        Assert.Null(buf.Selection);
        Assert.Empty(buf.SearchMatches);
    }

    [Fact]
    public void AltScreen_Reflow_DoesNotLeakRowsIntoScrollback()
    {
        // Alt-screen has ScrollbackLimit = 0 by design — content is
        // intentionally transient. A reflow that produces more
        // redistributed rows than fit in the live screen used to
        // quietly leak the overflow into the alt screen's ring
        // (capacity is internally clamped to >= 1), which would
        // surface as ghost rows once the user switched back to
        // primary.
        var buf = NewBuffer(20, 5);
        buf.Feed(CSI + "?1049h"); // enter alt-screen
        // Fill alt with auto-wrappable content that, on shrink, will
        // produce more redistributed rows than the alt screen can
        // hold.
        buf.Feed(new string('a', 40)); // wraps once at 20 cols
        buf.Resize(10, 5);             // narrow → produces ~4 rows
        // Inside alt-screen, the alt buffer's scrollback must stay
        // empty even though reflow generated more rows than fit.
        Assert.Equal(0, buf.ScrollbackCount);
    }

    [Fact]
    public void CursorRemap_OnEmptyBlankRow_StaysOnRowAfterReflow()
    {
        var buf = NewBuffer(20, 5);
        // Print two lines + a blank line + leave cursor on blank.
        buf.Feed("first\r\nsecond\r\n");
        // Cursor is now at row 2, col 0 — a blank logical line.
        Assert.Equal(2, buf.CursorRow);
        Assert.Equal(0, buf.CursorCol);
        buf.Resize(10, 5);
        // After reflow, the cursor SHOULD still be on a blank line
        // immediately following "second" — not pinned past all
        // content. With the empty-line cursor mapping fix it lands
        // on row 2; without it, cursor pinned to redistributed.Count
        // (3 → row 3 of live screen).
        Assert.Equal(2, buf.CursorRow);
    }

    [Fact]
    public void CursorRemap_AcrossWideCellWrapBoundary()
    {
        // At 4 cols print A中B (1 narrow + 1 wide + 1 narrow = 4 source
        // cells). Move cursor to right after B (col 4 → wraps to col 0
        // of next row deferred-wrap-style). Resize to 2 cols. Reflow
        // splits A中B into 3 rows: [A, _], [中, cont], [B, _]. Cursor
        // mapping must land at row 2 col 1 (just past B), not at
        // row 1 col 1 (which is the wide cell's continuation slot).
        var buf = NewBuffer(4, 3);
        buf.Feed("A中B");
        // Cursor is after B at col 4 (off-screen / deferred wrap).
        buf.Resize(2, 5);
        // After reflow we expect "A " on row 0, "中" (wide) on row 1
        // (cells [中, cont]), "B" on row 2. The cursor lands after B,
        // so row 2 col 1.
        Assert.Equal("A", buf.RowText(0).TrimEnd());
        // Row 1 has the wide char; trimming via TrimEnd may strip the
        // continuation, accept either rendering.
        Assert.Contains("中", buf.RowText(1));
        Assert.Equal("B", buf.RowText(2).TrimEnd());
        // Cursor sits at the column AFTER B on row 2.
        Assert.Equal(2, buf.CursorRow);
        Assert.True(buf.CursorCol == 1, $"cursor col was {buf.CursorCol}");
    }
}
