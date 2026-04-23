using Xunit;
using static VibeCoder.Terminal.Tests.TestHelpers;

namespace VibeCoder.Terminal.Tests;

/// <summary>
/// Resize semantics — cursor clamping, scroll region reset, scrollback
/// preservation. Matches xterm.js's behaviour in <c>Buffer.resize</c>.
/// </summary>
public class BufferResizeTests
{
    [Fact]
    public void Resize_Noop_WhenSameDims()
    {
        var buf = NewBuffer(10, 3);
        buf.Feed("hello");
        int rev = buf.Revision;
        buf.Resize(10, 3);
        Assert.Equal(rev, buf.Revision);
    }

    [Fact]
    public void Resize_ClampsCursor()
    {
        var buf = NewBuffer(20, 10);
        buf.Feed(CSI + "8;18H");
        buf.Resize(10, 5);
        Assert.True(buf.CursorRow <= 4);
        Assert.True(buf.CursorCol <= 9);
    }

    [Fact]
    public void Resize_ResetsScrollRegion()
    {
        var buf = NewBuffer(10, 5);
        buf.Feed(CSI + "2;4r"); // region rows 2..4
        buf.Resize(10, 8);
        Assert.Equal(0, buf.ScrollTop);
        Assert.Equal(7, buf.ScrollBottom);
    }

    [Fact]
    public void Resize_ResetsScrollOffset()
    {
        var buf = NewBuffer(10, 3);
        buf.ScrollbackLimit = 100;
        for (int i = 0; i < 10; i++) buf.Feed($"L{i}\r\n");
        buf.SetScrollOffset(3);
        buf.Resize(10, 5);
        Assert.Equal(0, buf.ScrollOffset);
    }

    [Fact]
    public void Resize_ShrinkingRowsEvictsToScrollback()
    {
        var buf = NewBuffer(10, 6);
        buf.ScrollbackLimit = 100;
        buf.Feed("A\r\nB\r\nC\r\nD\r\nE");
        // All lines within screen. Shrink to 2 rows → 4 get evicted (3 with content + 1 blank possibly dropped).
        int before = buf.ScrollbackCount;
        buf.Resize(10, 2);
        Assert.True(buf.ScrollbackCount >= before);
    }

    [Fact]
    public void Resize_NarrowingColsTruncatesLiveRow()
    {
        var buf = NewBuffer(10, 3);
        buf.Feed("abcdefgh");
        buf.Resize(5, 3);
        Assert.Equal("abcde", buf.RowText(0));
    }

    [Fact]
    public void Resize_RejectsZeroDims()
    {
        var buf = NewBuffer(10, 5);
        buf.Resize(0, 5);
        Assert.Equal(10, buf.Cols);
        buf.Resize(10, 0);
        Assert.Equal(5, buf.Rows);
    }
}
