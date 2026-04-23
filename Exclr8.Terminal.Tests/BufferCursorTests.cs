using Exclr8.Terminal.Buffer;
using Xunit;
using static Exclr8.Terminal.Tests.TestHelpers;

namespace Exclr8.Terminal.Tests;

/// <summary>
/// Cursor motion CSI sequences (CUP / CUU / CUD / CUF / CUB / CNL /
/// CPL / CHA / VPA). Plus edge-clamping and DECOM origin mode.
/// </summary>
public class BufferCursorTests
{
    [Fact]
    public void CUP_MovesTo1BasedPosition()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "3;5H"); // row 3, col 5 (1-based) → (2,4) 0-based
        Assert.Equal(2, buf.CursorRow);
        Assert.Equal(4, buf.CursorCol);
    }

    [Fact]
    public void CUP_DefaultsToHome()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "5;5H" + CSI + "H");
        Assert.Equal(0, buf.CursorRow);
        Assert.Equal(0, buf.CursorCol);
    }

    [Fact]
    public void HVP_SameAsCUP()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "2;3f");
        Assert.Equal(1, buf.CursorRow);
        Assert.Equal(2, buf.CursorCol);
    }

    [Fact]
    public void CUU_MovesUpWithDefault1()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "3;3H" + CSI + "A");
        Assert.Equal(1, buf.CursorRow);
    }

    [Fact]
    public void CUU_MovesUpWithParam()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "4;1H" + CSI + "2A");
        Assert.Equal(1, buf.CursorRow);
    }

    [Fact]
    public void CUU_ClampsAtTop()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "1;1H" + CSI + "5A");
        Assert.Equal(0, buf.CursorRow);
    }

    [Fact]
    public void CUD_MovesDownWithParam()
    {
        var buf = NewBuffer(rows: 6);
        buf.Feed(CSI + "1;1H" + CSI + "2B");
        Assert.Equal(2, buf.CursorRow);
    }

    [Fact]
    public void CUD_ClampsAtBottom()
    {
        var buf = NewBuffer(rows: 4);
        buf.Feed(CSI + "1;1H" + CSI + "99B");
        Assert.Equal(3, buf.CursorRow);
    }

    [Fact]
    public void CUF_MovesRightWithParam()
    {
        var buf = NewBuffer(cols: 10);
        buf.Feed(CSI + "1;1H" + CSI + "3C");
        Assert.Equal(3, buf.CursorCol);
    }

    [Fact]
    public void CUF_ClampsAtRight()
    {
        var buf = NewBuffer(cols: 10);
        buf.Feed(CSI + "1;1H" + CSI + "99C");
        Assert.Equal(9, buf.CursorCol);
    }

    [Fact]
    public void CUB_MovesLeftWithParam()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "1;5H" + CSI + "2D");
        Assert.Equal(2, buf.CursorCol);
    }

    [Fact]
    public void CUB_ClampsAtLeft()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "1;1H" + CSI + "5D");
        Assert.Equal(0, buf.CursorCol);
    }

    [Fact]
    public void CNL_MovesDownToColumn1()
    {
        // CSI Ps E → next line N, column 1
        var buf = NewBuffer(rows: 6);
        buf.Feed(CSI + "1;5H" + CSI + "2E");
        Assert.Equal(2, buf.CursorRow);
        Assert.Equal(0, buf.CursorCol);
    }

    [Fact]
    public void CPL_MovesUpToColumn1()
    {
        // CSI Ps F → previous line N, column 1
        var buf = NewBuffer(rows: 6);
        buf.Feed(CSI + "4;5H" + CSI + "2F");
        Assert.Equal(1, buf.CursorRow);
        Assert.Equal(0, buf.CursorCol);
    }

    [Fact]
    public void CHA_AbsoluteColumn()
    {
        // CSI Ps G (HPA) → set column, 1-based
        var buf = NewBuffer();
        buf.Feed(CSI + "2;2H" + CSI + "7G");
        Assert.Equal(6, buf.CursorCol);
        Assert.Equal(1, buf.CursorRow);
    }

    [Fact]
    public void VPA_AbsoluteRow()
    {
        // CSI Ps d → set row, 1-based
        var buf = NewBuffer(rows: 6);
        buf.Feed(CSI + "1;3H" + CSI + "4d");
        Assert.Equal(3, buf.CursorRow);
        Assert.Equal(2, buf.CursorCol);
    }

    // ------------------------------------------------------------------
    // Control chars that affect cursor position
    // ------------------------------------------------------------------

    [Fact]
    public void CR_MovesToColumn0()
    {
        var buf = NewBuffer();
        buf.Feed("abcdef\r");
        Assert.Equal(0, buf.CursorCol);
        // Row unchanged.
        Assert.Equal(0, buf.CursorRow);
    }

    [Fact]
    public void LF_AdvancesRow()
    {
        var buf = NewBuffer(rows: 4);
        buf.Feed("a\nb");
        Assert.Equal(1, buf.CursorRow);
    }

    [Fact]
    public void BS_DecrementsColumnClamped()
    {
        var buf = NewBuffer();
        buf.Feed("abc\b\b");
        Assert.Equal(1, buf.CursorCol);
    }

    [Fact]
    public void BS_AtColumn0StaysAt0()
    {
        var buf = NewBuffer();
        buf.Feed("\b\b");
        Assert.Equal(0, buf.CursorCol);
    }

    // ------------------------------------------------------------------
    // DECSC / DECRC (ESC 7 / ESC 8)
    // ------------------------------------------------------------------

    [Fact]
    public void DECSC_DECRC_RestoresPosition()
    {
        var buf = NewBuffer(cols: 20, rows: 10);
        buf.Feed(CSI + "5;10H");
        buf.FeedBytes(0x1B, (byte)'7'); // DECSC
        buf.Feed(CSI + "1;1H");
        buf.FeedBytes(0x1B, (byte)'8'); // DECRC
        Assert.Equal(4, buf.CursorRow);
        Assert.Equal(9, buf.CursorCol);
    }

    [Fact]
    public void CSI_s_u_SaveRestoreSamAsDECSC()
    {
        var buf = NewBuffer(cols: 20, rows: 10);
        buf.Feed(CSI + "3;7H" + CSI + "s");
        buf.Feed(CSI + "1;1H" + CSI + "u");
        Assert.Equal(2, buf.CursorRow);
        Assert.Equal(6, buf.CursorCol);
    }

    // ------------------------------------------------------------------
    // Out-of-range defensive clamping
    // ------------------------------------------------------------------

    [Fact]
    public void CUP_OutOfBoundsClampsToEdges()
    {
        var buf = NewBuffer(cols: 10, rows: 5);
        buf.Feed(CSI + "99;99H");
        Assert.Equal(4, buf.CursorRow);
        Assert.Equal(9, buf.CursorCol);
    }
}
