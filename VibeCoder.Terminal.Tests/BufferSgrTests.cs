using VibeCoder.Terminal.Buffer;
using Xunit;
using static VibeCoder.Terminal.Tests.TestHelpers;

namespace VibeCoder.Terminal.Tests;

/// <summary>
/// SGR (select graphic rendition): colors + attribute toggles. Mirrors
/// the xterm.js matrix in <c>InputHandler.processSGR</c>.
/// </summary>
public class BufferSgrTests
{
    private static TerminalCell CellAt(TerminalBuffer b, int r, int c)
        => b.GetVisibleRow(r)[c];

    [Fact]
    public void SGR_0_ResetsAttributes()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "1;31mX" + CSI + "0mY");
        var x = CellAt(buf, 0, 0);
        var y = CellAt(buf, 0, 1);
        // X carries bold + red.
        Assert.True((x.Flags & CellFlags.Bold) != 0);
        Assert.Equal(1, x.FgIndex);
        // Y is back to defaults after SGR 0.
        Assert.False((y.Flags & CellFlags.Bold) != 0);
        Assert.Equal(0, y.FgIndex);
    }

    [Fact]
    public void SGR_1_2_3_4_7_9_SetTextAttrs()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "1;2;3;4;7;9mA");
        var c = CellAt(buf, 0, 0);
        Assert.True((c.Flags & CellFlags.Bold)          != 0);
        Assert.True((c.Flags & CellFlags.Dim)           != 0);
        Assert.True((c.Flags & CellFlags.Italic)        != 0);
        Assert.True((c.Flags & CellFlags.Underline)     != 0);
        Assert.True((c.Flags & CellFlags.Inverse)       != 0);
        Assert.True((c.Flags & CellFlags.Strikethrough) != 0);
    }

    [Fact]
    public void SGR_22_ClearsBoldAndDim()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "1;2mA" + CSI + "22mB");
        var b = CellAt(buf, 0, 1);
        Assert.False((b.Flags & CellFlags.Bold) != 0);
        Assert.False((b.Flags & CellFlags.Dim)  != 0);
    }

    [Fact]
    public void SGR_BasicFg_30_37()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "33mX"); // yellow
        Assert.Equal(3, CellAt(buf, 0, 0).FgIndex);
    }

    [Fact]
    public void SGR_BrightFg_90_97()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "93mX"); // bright yellow
        Assert.Equal(11, CellAt(buf, 0, 0).FgIndex);
    }

    [Fact]
    public void SGR_BasicBg_40_47()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "44mX"); // blue bg
        Assert.Equal(4, CellAt(buf, 0, 0).BgIndex);
    }

    [Fact]
    public void SGR_BrightBg_100_107()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "104mX");
        Assert.Equal(12, CellAt(buf, 0, 0).BgIndex);
    }

    [Fact]
    public void SGR_256ColorFg()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "38;5;214mX");
        Assert.Equal(214, CellAt(buf, 0, 0).FgIndex);
    }

    [Fact]
    public void SGR_256ColorBg()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "48;5;160mX");
        Assert.Equal(160, CellAt(buf, 0, 0).BgIndex);
    }

    [Fact]
    public void SGR_TrueColorFg()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "38;2;255;128;0mX");
        var c = CellAt(buf, 0, 0);
        Assert.True((c.Flags & CellFlags.FgRgb) != 0);
        Assert.Equal(0xFF8000u, c.FgRgb);
    }

    [Fact]
    public void SGR_TrueColorBg()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "48;2;10;20;30mX");
        var c = CellAt(buf, 0, 0);
        Assert.True((c.Flags & CellFlags.BgRgb) != 0);
        Assert.Equal((uint)((10 << 16) | (20 << 8) | 30), c.BgRgb);
    }

    [Fact]
    public void SGR_39_ResetsFgIndexToDefault()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "31mA" + CSI + "39mB");
        Assert.Equal(0, CellAt(buf, 0, 1).FgIndex);
    }

    [Fact]
    public void SGR_49_ResetsBgIndexToDefault()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "41mA" + CSI + "49mB");
        Assert.Equal(0, CellAt(buf, 0, 1).BgIndex);
    }

    [Fact]
    public void SGR_EmptyParamsResetsLikeSGR0()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "1;31mA" + CSI + "mB");
        var b = CellAt(buf, 0, 1);
        Assert.False((b.Flags & CellFlags.Bold) != 0);
        Assert.Equal(0, b.FgIndex);
    }

    [Fact]
    public void SGR_UnderlineThenClear()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "4mA" + CSI + "24mB");
        Assert.True((CellAt(buf, 0, 0).Flags & CellFlags.Underline) != 0);
        Assert.False((CellAt(buf, 0, 1).Flags & CellFlags.Underline) != 0);
    }

    [Fact]
    public void SGR_InverseThenClear()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "7mA" + CSI + "27mB");
        Assert.True((CellAt(buf, 0, 0).Flags & CellFlags.Inverse) != 0);
        Assert.False((CellAt(buf, 0, 1).Flags & CellFlags.Inverse) != 0);
    }

    [Fact]
    public void SGR_StrikethroughThenClear()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "9mA" + CSI + "29mB");
        Assert.True((CellAt(buf, 0, 0).Flags & CellFlags.Strikethrough) != 0);
        Assert.False((CellAt(buf, 0, 1).Flags & CellFlags.Strikethrough) != 0);
    }
}
