using Xunit;
using static VibeCoder.Terminal.Tests.TestHelpers;

namespace VibeCoder.Terminal.Tests;

/// <summary>
/// Miscellaneous control-sequence behaviour: RI at top of region, IND,
/// NEL, DECSC/DECRC semantic parity with CSI s/u, cursor out-of-region
/// clamping.
/// </summary>
public class BufferControlTests
{
    [Fact]
    public void IND_LineFeed()
    {
        var buf = NewBuffer(5, 3);
        buf.Feed("A");
        buf.FeedBytes(0x1B, (byte)'D'); // IND → LF
        Assert.Equal(1, buf.CursorRow);
    }

    [Fact]
    public void NEL_CursorReturnsToColumn0AndAdvances()
    {
        var buf = NewBuffer(5, 3);
        buf.Feed("ABC");
        buf.FeedBytes(0x1B, (byte)'E'); // NEL → CR + LF
        Assert.Equal(1, buf.CursorRow);
        Assert.Equal(0, buf.CursorCol);
    }

    [Fact]
    public void RI_AtTopOfScreenScrollsDown()
    {
        var buf = NewBuffer(5, 3);
        buf.Feed("A\r\nB\r\nC");
        buf.Feed(CSI + "1;1H"); // top
        buf.FeedBytes(0x1B, (byte)'M'); // RI
        Assert.Equal("",  buf.RowText(0));
        Assert.Equal("A", buf.RowText(1));
        Assert.Equal("B", buf.RowText(2));
    }

    [Fact]
    public void FullReset_ReturnsToDefaults()
    {
        var buf = NewBuffer(10, 3);
        buf.Feed("HELLO" + CSI + "1;31m" + CSI + "?25l");
        buf.FeedBytes(0x1B, (byte)'c'); // RIS
        Assert.Equal("", buf.RowText(0));
        Assert.True(buf.CursorVisible);
        Assert.Equal(0, buf.CursorRow);
        Assert.Equal(0, buf.CursorCol);
    }

    [Fact]
    public void BracketedPaste_ParsedAsBytesByParser()
    {
        // When bracketed paste is enabled the paste bytes flow through
        // the parser — the bracketing is visible as plain printable
        // text in the buffer (the shell would otherwise intercept).
        // We're testing that the raw sequence doesn't confuse the parser.
        var buf = NewBuffer(30, 2);
        buf.Feed(CSI + "?2004h");
        Assert.True(buf.BracketedPaste);
        // CSI 200~ and CSI 201~ are CSI sequences the shell reacts to —
        // a non-shell terminal just dispatches them as no-op CSIs.
        // Ensure they don't derail the parser.
        buf.Feed(CSI + "200~hello" + CSI + "201~");
        // "hello" printable, brackets consumed as CSI no-ops.
        Assert.Equal("hello", buf.RowText(0));
    }

    [Fact]
    public void CursorTabStopsAtEvery8Columns()
    {
        var buf = NewBuffer(40, 2);
        buf.FeedBytes(0x09); // HT at col 0 → col 8
        Assert.Equal(8, buf.CursorCol);
        buf.FeedBytes(0x09); // col 16
        Assert.Equal(16, buf.CursorCol);
    }

    [Fact]
    public void CursorTabAdvancesToNextMultipleOfEight()
    {
        var buf = NewBuffer(10, 2);
        buf.Feed(CSI + "1;8H"); // col 7
        buf.FeedBytes(0x09);
        Assert.Equal(8, buf.CursorCol);
    }

    [Fact]
    public void CursorTabClampsAtRightEdge()
    {
        var buf = NewBuffer(10, 2);
        buf.Feed(CSI + "1;10H"); // col 9 — already at right edge
        buf.FeedBytes(0x09);
        Assert.Equal(9, buf.CursorCol);
    }
}
