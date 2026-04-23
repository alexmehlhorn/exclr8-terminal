using Exclr8.Terminal.Buffer;
using Xunit;
using static Exclr8.Terminal.Tests.TestHelpers;

namespace Exclr8.Terminal.Tests;

/// <summary>
/// DEC Special Graphics charset (ESC ( 0 / ESC ) 0) + SO/SI. Used by
/// htop, tmux, and ncurses borders.
/// </summary>
public class BufferCharsetTests
{
    [Fact]
    public void SCS_G0_DecGraphicsTranslatesAscii()
    {
        var buf = NewBuffer(10, 2);
        buf.FeedBytes(0x1B, (byte)'(', (byte)'0'); // ESC ( 0 → G0 = DEC graphics
        buf.Feed("lqwk"); // corner chars
        var row = buf.GetVisibleRow(0);
        // l=0x250C (TL corner), q=0x2500 (horiz), w=0x252C, k=0x2510
        Assert.Equal(0x250C, row[0].Rune);
        Assert.Equal(0x2500, row[1].Rune);
        Assert.Equal(0x252C, row[2].Rune);
        Assert.Equal(0x2510, row[3].Rune);
    }

    [Fact]
    public void SCS_G0_AsciiRestoresLetters()
    {
        var buf = NewBuffer(10, 2);
        buf.FeedBytes(0x1B, (byte)'(', (byte)'0');
        buf.Feed("l");
        buf.FeedBytes(0x1B, (byte)'(', (byte)'B'); // ESC ( B → ASCII
        buf.Feed("l");
        var row = buf.GetVisibleRow(0);
        Assert.Equal(0x250C, row[0].Rune);
        Assert.Equal('l',    row[1].Rune);
    }

    [Fact]
    public void SO_SI_SwitchBetweenG0AndG1()
    {
        var buf = NewBuffer(10, 2);
        buf.FeedBytes(0x1B, (byte)')', (byte)'0'); // G1 = DEC graphics
        buf.Feed("l");                              // G0 is ASCII → 'l'
        buf.FeedBytes(0x0E);                        // SO → G1 active
        buf.Feed("l");                              // now graphics
        buf.FeedBytes(0x0F);                        // SI → G0 active
        buf.Feed("l");
        var row = buf.GetVisibleRow(0);
        Assert.Equal('l',    row[0].Rune);
        Assert.Equal(0x250C, row[1].Rune);
        Assert.Equal('l',    row[2].Rune);
    }

    [Fact]
    public void WideChar_OccupiesTwoColumns()
    {
        var buf = NewBuffer(10, 2);
        buf.Feed("A中B"); // 中 is wide
        var row = buf.GetVisibleRow(0);
        Assert.Equal('A',    row[0].Rune);
        Assert.Equal('中',   row[1].Rune);
        Assert.True((row[1].Flags2 & CellFlags2.IsWide) != 0);
        Assert.True((row[2].Flags2 & CellFlags2.IsContinuation) != 0);
        Assert.Equal('B', row[3].Rune);
    }

    [Fact]
    public void WideChar_WrapsAtRightEdge()
    {
        var buf = NewBuffer(3, 2);
        buf.Feed("AB中"); // A, B fill first two cols; 中 wants 2 → wraps
        var row0 = buf.GetVisibleRow(0);
        var row1 = buf.GetVisibleRow(1);
        Assert.Equal('A', row0[0].Rune);
        Assert.Equal('B', row0[1].Rune);
        Assert.Equal('中', row1[0].Rune);
    }
}
