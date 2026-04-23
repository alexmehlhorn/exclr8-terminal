using Xunit;
using static Exclr8.Terminal.Tests.TestHelpers;

namespace Exclr8.Terminal.Tests;

/// <summary>
/// DECSTBM + region-aware scroll semantics. Content outside the
/// scroll region must not move when the region scrolls.
/// </summary>
public class BufferScrollRegionTests
{
    [Fact]
    public void DECSTBM_ParksCursorAtHome()
    {
        var buf = NewBuffer(10, 6);
        buf.Feed(CSI + "3;3H");
        buf.Feed(CSI + "2;4r");
        Assert.Equal(0, buf.CursorRow);
        Assert.Equal(0, buf.CursorCol);
    }

    [Fact]
    public void DECSTBM_SetsRegion()
    {
        var buf = NewBuffer(10, 6);
        buf.Feed(CSI + "2;5r");
        Assert.Equal(1, buf.ScrollTop);
        Assert.Equal(4, buf.ScrollBottom);
    }

    [Fact]
    public void DECSTBM_InvalidResetsToFullScreen()
    {
        var buf = NewBuffer(10, 6);
        buf.Feed(CSI + "5;2r"); // top > bottom → reset
        Assert.Equal(0, buf.ScrollTop);
        Assert.Equal(5, buf.ScrollBottom);
    }

    [Fact]
    public void DECSTBM_DefaultArgsResetRegion()
    {
        var buf = NewBuffer(10, 6);
        buf.Feed(CSI + "2;5r" + CSI + "r");
        Assert.Equal(0, buf.ScrollTop);
        Assert.Equal(5, buf.ScrollBottom);
    }

    [Fact]
    public void LF_AtRegionBottomScrollsRegionOnly()
    {
        var buf = NewBuffer(5, 5);
        buf.Feed("A\r\nB\r\nC\r\nD\r\nE");
        // Screen: A / B / C / D / E
        buf.Feed(CSI + "2;4r");       // region rows 2..4 (0-based 1..3)
        buf.Feed(CSI + "4;1H");        // row 4 col 1 → bottom of region
        buf.Feed("\nX");               // LF should scroll region: B,C,D become C,D,<blank>; then X at col 1 of new blank row
        Assert.Equal("A", buf.RowText(0));
        Assert.Equal("C", buf.RowText(1));
        Assert.Equal("D", buf.RowText(2));
        Assert.Equal("X", buf.RowText(3));
        Assert.Equal("E", buf.RowText(4));
    }

    [Fact]
    public void RI_AtRegionTopScrollsRegionDown()
    {
        var buf = NewBuffer(5, 5);
        buf.Feed("A\r\nB\r\nC\r\nD\r\nE");
        buf.Feed(CSI + "2;4r");
        buf.Feed(CSI + "2;1H");         // row 2 (0-based 1) = top of region
        buf.FeedBytes(0x1B, (byte)'M'); // RI
        // Now: A / <blank> / B / C / E
        Assert.Equal("A", buf.RowText(0));
        Assert.Equal("",  buf.RowText(1));
        Assert.Equal("B", buf.RowText(2));
        Assert.Equal("C", buf.RowText(3));
        Assert.Equal("E", buf.RowText(4));
    }

    [Fact]
    public void ScrollUpCSI_S_InRegion()
    {
        var buf = NewBuffer(5, 5);
        buf.Feed("A\r\nB\r\nC\r\nD\r\nE");
        buf.Feed(CSI + "2;4r" + CSI + "S");
        // Region 1..3 (0-based) scrolls up by 1 → C/D/<blank>
        Assert.Equal("A", buf.RowText(0));
        Assert.Equal("C", buf.RowText(1));
        Assert.Equal("D", buf.RowText(2));
        Assert.Equal("",  buf.RowText(3));
        Assert.Equal("E", buf.RowText(4));
    }

    [Fact]
    public void ScrollDownCSI_T_InRegion()
    {
        var buf = NewBuffer(5, 5);
        buf.Feed("A\r\nB\r\nC\r\nD\r\nE");
        buf.Feed(CSI + "2;4r" + CSI + "T");
        // Region scrolls down → <blank>/B/C inside region
        Assert.Equal("A", buf.RowText(0));
        Assert.Equal("",  buf.RowText(1));
        Assert.Equal("B", buf.RowText(2));
        Assert.Equal("C", buf.RowText(3));
        Assert.Equal("E", buf.RowText(4));
    }

    [Fact]
    public void FullScreenScrollUp_EvictsToScrollback()
    {
        var buf = NewBuffer(5, 3);
        buf.ScrollbackLimit = 100;
        buf.Feed("AAA\r\nBBB\r\nCCC\r\nDDD");
        // Last LF should evict AAA into scrollback.
        Assert.True(buf.ScrollbackCount >= 1);
    }

    [Fact]
    public void RegionScrollUp_DoesNotEvictToScrollback()
    {
        var buf = NewBuffer(5, 5);
        buf.ScrollbackLimit = 100;
        buf.Feed("A\r\nB\r\nC\r\nD\r\nE");
        int before = buf.ScrollbackCount;
        buf.Feed(CSI + "2;4r" + CSI + "5S"); // 5 lines: saturates at region height
        Assert.Equal(before, buf.ScrollbackCount);
    }
}
