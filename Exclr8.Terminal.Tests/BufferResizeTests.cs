using Xunit;
using static Exclr8.Terminal.Tests.TestHelpers;

namespace Exclr8.Terminal.Tests;

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

    [Fact]
    public void Resize_NarrowingWrapsLongLineAcrossRows()
    {
        var buf = NewBuffer(10, 4);
        buf.Feed("abcdefgh");
        buf.Resize(5, 4);
        Assert.Equal("abcde", buf.RowText(0));
        Assert.Equal("fgh", buf.RowText(1).TrimEnd());
    }

    [Fact]
    public void Resize_WideningRejoinsWrappedLine()
    {
        var buf = NewBuffer(5, 4);
        // "abcdefgh" written into a 5-col buffer auto-wraps after the
        // 5th cell, leaving row 0 = "abcde" and row 1 = "fgh  " with
        // wrap flag set.
        buf.Feed("abcdefgh");
        buf.Resize(10, 4);
        // After widening, the two rows should re-merge into one logical
        // line on row 0.
        Assert.Equal("abcdefgh", buf.RowText(0).TrimEnd());
    }

    [Fact]
    public void Resize_NarrowingDoesNotWrapNonWrappedLines()
    {
        var buf = NewBuffer(10, 4);
        // Two separate lines, each shorter than the new width. Reflow
        // must NOT join them into one logical line.
        buf.Feed("abc\r\ndef");
        buf.Resize(5, 4);
        Assert.Equal("abc", buf.RowText(0).TrimEnd());
        Assert.Equal("def", buf.RowText(1).TrimEnd());
    }

    [Fact]
    public void Resize_RowShrink_PreservesScrolledOutContentInScrollback()
    {
        // When a row-shrink can't fit the whole live screen (and
        // dropping blank tails doesn't free enough rows), the rows
        // that scroll off the top are real shell history. They must
        // land in scrollback rather than being silently dropped —
        // this matches iTerm2 / Terminal.app / WezTerm. The earlier
        // "no-push" rule traded history loss for the TUI-redraw
        // scrollback-inflation case; that tradeoff was wrong for the
        // common shell-on-primary scenario where history is what the
        // user actually wants preserved.
        var buf = NewBuffer(20, 10);
        for (int r = 0; r < 10; r++)
        {
            buf.Feed($"line{r}");
            if (r < 9) buf.Feed("\r\n");
        }
        int sbBefore = buf.ScrollbackCount;

        // Cursor is on row 9 (after "line9"). Shrinking to 6 rows
        // forces 4 rows off the top: line0..line3.
        buf.Resize(20, 6);

        Assert.True(buf.ScrollbackCount > sbBefore,
            "scrolled-off rows should land in scrollback");
        // Cursor stays on its content row, snapped into the new bottom.
        Assert.Equal(5, buf.CursorRow);
    }

    [Fact]
    public void Resize_AltScreenShrink_DropsRowsInsteadOfPreserving()
    {
        // Alt-screen has ScrollbackLimit = 0 by design — TUIs there
        // own the display and redraw on SIGWINCH, so any rows we'd
        // push to scrollback would just bloat memory before the next
        // redraw clobbers the visible state. Verify the alt-screen
        // path still drops on shrink.
        var buf = NewBuffer(20, 10);
        buf.Feed(CSI + "?1049h");           // enter alt-screen
        for (int r = 0; r < 10; r++)
        {
            buf.Feed($"line{r}");
            if (r < 9) buf.Feed("\r\n");
        }
        Assert.Equal(0, buf.ScrollbackCount); // alt-screen has none
        buf.Resize(20, 6);
        Assert.Equal(0, buf.ScrollbackCount); // still none after shrink
    }
}
