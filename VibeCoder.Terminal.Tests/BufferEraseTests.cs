using Xunit;
using static VibeCoder.Terminal.Tests.TestHelpers;

namespace VibeCoder.Terminal.Tests;

/// <summary>
/// ED / EL / ICH / DCH / ECH / IL / DL — the erase and insert/delete
/// family.
/// </summary>
public class BufferEraseTests
{
    // ---- ED: CSI Ps J ----

    [Fact]
    public void ED_0_ErasesFromCursorToEndOfScreen()
    {
        var buf = NewBuffer(10, 3);
        buf.Feed("ABCDE\r\nFGHIJ\r\nKLMNO");
        // Cursor is at end of third line (col 5, row 2).
        buf.Feed(CSI + "2;3H" + CSI + "0J"); // row 2 col 3, erase display from here
        Assert.Equal("ABCDE\nFG\n", buf.ScreenText());
    }

    [Fact]
    public void ED_1_ErasesFromStartToCursor()
    {
        var buf = NewBuffer(10, 3);
        buf.Feed("ABCDE\r\nFGHIJ\r\nKLMNO");
        buf.Feed(CSI + "2;3H" + CSI + "1J");
        // Rows 0 cleared, row 1 cleared through col 2 inclusive → " " * 3 + HIJ.
        Assert.Equal("", buf.RowText(0));
        Assert.Equal("   IJ", buf.RowText(1));
        Assert.Equal("KLMNO", buf.RowText(2));
    }

    [Fact]
    public void ED_2_ErasesAllRows()
    {
        var buf = NewBuffer(10, 3);
        buf.Feed("ABCDE\r\nFGHIJ\r\nKLMNO");
        buf.Feed(CSI + "2J");
        Assert.Equal("", buf.RowText(0));
        Assert.Equal("", buf.RowText(1));
        Assert.Equal("", buf.RowText(2));
    }

    [Fact(Skip = "Phase 2: ED 3 (clear scrollback) not yet implemented")]
    public void ED_3_ClearsAllIncludingScrollback()
    {
        var buf = NewBuffer(10, 3);
        buf.ScrollbackLimit = 100;
        // Force scrollback by printing more lines than rows.
        buf.Feed("A\r\nB\r\nC\r\nD\r\nE\r\nF");
        Assert.True(buf.ScrollbackCount > 0);
        buf.Feed(CSI + "3J");
        Assert.Equal(0, buf.ScrollbackCount);
    }

    // ---- EL: CSI Ps K ----

    [Fact]
    public void EL_0_ErasesFromCursorToEndOfLine()
    {
        var buf = NewBuffer(10, 2);
        buf.Feed("ABCDEFGH");
        buf.Feed(CSI + "1;4H" + CSI + "K");
        Assert.Equal("ABC", buf.RowText(0));
    }

    [Fact]
    public void EL_1_ErasesFromStartOfLineToCursor()
    {
        var buf = NewBuffer(10, 2);
        buf.Feed("ABCDEFGH");
        buf.Feed(CSI + "1;4H" + CSI + "1K");
        Assert.Equal("    EFGH", buf.RowText(0));
    }

    [Fact]
    public void EL_2_ErasesEntireLine()
    {
        var buf = NewBuffer(10, 2);
        buf.Feed("ABCDEFGH");
        buf.Feed(CSI + "1;4H" + CSI + "2K");
        Assert.Equal("", buf.RowText(0));
    }

    // ---- ICH: CSI Ps @ ----

    [Fact]
    public void ICH_InsertsBlankCellsShiftingRight()
    {
        var buf = NewBuffer(10, 2);
        buf.Feed("ABCDEF");
        buf.Feed(CSI + "1;3H" + CSI + "2@"); // insert 2 at col 3
        Assert.Equal("AB  CDEF", buf.RowText(0));
    }

    // ---- DCH: CSI Ps P ----

    [Fact]
    public void DCH_DeletesCellsShiftingLeft()
    {
        var buf = NewBuffer(10, 2);
        buf.Feed("ABCDEF");
        buf.Feed(CSI + "1;3H" + CSI + "2P");
        Assert.Equal("ABEF", buf.RowText(0));
    }

    // ---- ECH: CSI Ps X ----

    [Fact]
    public void ECH_ReplacesCellsWithBlankNoShift()
    {
        var buf = NewBuffer(10, 2);
        buf.Feed("ABCDEF");
        buf.Feed(CSI + "1;3H" + CSI + "2X");
        Assert.Equal("AB  EF", buf.RowText(0));
    }

    // ---- IL: CSI Ps L ----

    [Fact]
    public void IL_InsertsBlankLines()
    {
        var buf = NewBuffer(5, 4);
        buf.Feed("A\r\nB\r\nC\r\nD");
        // Cursor now at (3,1). Move to row 2 and insert a line.
        buf.Feed(CSI + "2;1H" + CSI + "L");
        Assert.Equal("A", buf.RowText(0));
        Assert.Equal("", buf.RowText(1));
        Assert.Equal("B", buf.RowText(2));
        Assert.Equal("C", buf.RowText(3));
    }

    // ---- DL: CSI Ps M ----

    [Fact]
    public void DL_DeletesLines()
    {
        var buf = NewBuffer(5, 4);
        buf.Feed("A\r\nB\r\nC\r\nD");
        buf.Feed(CSI + "2;1H" + CSI + "M"); // delete line at row 2
        Assert.Equal("A", buf.RowText(0));
        Assert.Equal("C", buf.RowText(1));
        Assert.Equal("D", buf.RowText(2));
        Assert.Equal("", buf.RowText(3));
    }

    [Fact]
    public void IL_RespectsScrollBottom()
    {
        // With DECSTBM 1..3, inserting at row 2 should not push row 3's
        // content — it stays as-is (row 3 is outside region).
        var buf = NewBuffer(5, 4);
        buf.Feed("A\r\nB\r\nC\r\nD");
        buf.Feed(CSI + "1;3r"); // scroll region rows 1..3 (1-based)
        buf.Feed(CSI + "2;1H" + CSI + "L");
        Assert.Equal("D", buf.RowText(3));
    }
}
