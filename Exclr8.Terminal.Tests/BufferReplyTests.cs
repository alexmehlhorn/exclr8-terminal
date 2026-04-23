using Xunit;
using static Exclr8.Terminal.Tests.TestHelpers;

namespace Exclr8.Terminal.Tests;

/// <summary>
/// DSR (Device Status Report) + DA (Device Attributes) reply
/// generation. The buffer queues replies internally; the host drains
/// them via <c>TakeReplies()</c>.
/// </summary>
public class BufferReplyTests
{
    [Fact]
    public void DSR_5_StatusReportReplies0n()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "5n");
        Assert.Equal(CSI + "0n", buf.TakeRepliesAscii());
    }

    [Fact]
    public void DSR_6_CursorPositionReport()
    {
        var buf = NewBuffer(10, 5);
        buf.Feed(CSI + "3;7H");
        buf.Feed(CSI + "6n");
        // 1-based row/col
        Assert.Equal(CSI + "3;7R", buf.TakeRepliesAscii());
    }

    [Fact]
    public void DA1_RepliesWithVT220Capabilities()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "c");
        var reply = buf.TakeRepliesAscii();
        // Current impl: ESC[?62;4;22c  — VT220 + sixel + ANSI color
        Assert.Contains("?62", reply);
        Assert.EndsWith("c", reply);
    }

    [Fact]
    public void DSR_DrainsQueueOnce()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "5n");
        _ = buf.TakeRepliesAscii();
        // Second drain should be empty.
        Assert.Equal(string.Empty, buf.TakeRepliesAscii());
    }
}
