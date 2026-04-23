using Exclr8.Terminal.Buffer;
using Xunit;
using static Exclr8.Terminal.Tests.TestHelpers;

namespace Exclr8.Terminal.Tests;

/// <summary>
/// Scrollback viewport math: ScrollOffset/PixelScrollOffset, smooth
/// pixel scrolling, GetRowForRender at viewport edges.
/// </summary>
public class BufferScrollViewTests
{
    private static TerminalBuffer BufferWithScrollback()
    {
        var buf = NewBuffer(10, 3);
        buf.ScrollbackLimit = 100;
        // Print 10 lines → 7 evicted into scrollback.
        for (int i = 0; i < 10; i++)
            buf.Feed($"L{i:00}\r\n");
        return buf;
    }

    [Fact]
    public void ScrollOffset_StartsAtZero()
    {
        var buf = NewBuffer();
        Assert.Equal(0, buf.ScrollOffset);
    }

    [Fact]
    public void SetScrollOffset_ClampsToScrollbackBounds()
    {
        var buf = BufferWithScrollback();
        buf.SetScrollOffset(9999);
        Assert.Equal(buf.ScrollbackCount, buf.ScrollOffset);
        buf.SetScrollOffset(-5);
        Assert.Equal(0, buf.ScrollOffset);
    }

    [Fact]
    public void ScrollByPixels_AccumulatesSubLine()
    {
        var buf = BufferWithScrollback();
        buf.ScrollByPixels(5.0, 20.0); // 5 px of a 20-px line
        Assert.Equal(0, buf.ScrollOffset);
        Assert.Equal(5.0, buf.PixelScrollOffset, 3);
    }

    [Fact]
    public void ScrollByPixels_PromotesToWholeLine()
    {
        var buf = BufferWithScrollback();
        buf.ScrollByPixels(25.0, 20.0); // 1 line + 5 px
        Assert.Equal(1, buf.ScrollOffset);
        Assert.Equal(5.0, buf.PixelScrollOffset, 3);
    }

    [Fact]
    public void ScrollByPixels_ClampsAtBottom()
    {
        var buf = BufferWithScrollback();
        buf.ScrollByPixels(-9999, 20.0);
        Assert.Equal(0, buf.ScrollOffset);
        Assert.Equal(0.0, buf.PixelScrollOffset, 3);
    }

    [Fact]
    public void ScrollByPixels_ClampsAtTop()
    {
        var buf = BufferWithScrollback();
        buf.ScrollByPixels(99999, 20.0);
        Assert.Equal(buf.ScrollbackCount, buf.ScrollOffset);
    }

    [Fact]
    public void ResetScrollOffset_ReturnsToLiveScreen()
    {
        var buf = BufferWithScrollback();
        buf.SetScrollOffset(3);
        buf.ResetScrollOffset();
        Assert.Equal(0, buf.ScrollOffset);
        Assert.Equal(0.0, buf.PixelScrollOffset, 3);
    }

    [Fact]
    public void GetRowForRender_OffsetZero_ReturnsLiveRow()
    {
        var buf = BufferWithScrollback();
        Assert.NotNull(buf.GetRowForRender(0));
        Assert.NotNull(buf.GetRowForRender(buf.Rows - 1));
    }

    [Fact]
    public void GetRowForRender_NegativeVisualAboveScrollback_Null()
    {
        var buf = BufferWithScrollback();
        // Even when scrolled all the way up, there's no row above row -N.
        buf.SetScrollOffset(buf.ScrollbackCount);
        Assert.Null(buf.GetRowForRender(-1));
    }

    [Fact]
    public void GetRowForRender_NegativeOne_WhenNotAtTop_ReturnsScrollback()
    {
        var buf = BufferWithScrollback();
        // Middle scroll: one row of scrollback is visible above.
        buf.SetScrollOffset(buf.ScrollbackCount / 2);
        Assert.NotNull(buf.GetRowForRender(-1));
    }

    [Fact]
    public void ClearScrollback_DropsAllHistory()
    {
        var buf = BufferWithScrollback();
        Assert.True(buf.ScrollbackCount > 0);
        buf.ClearScrollback();
        Assert.Equal(0, buf.ScrollbackCount);
        Assert.Equal(0, buf.ScrollOffset);
    }
}
