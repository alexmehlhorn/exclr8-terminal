using Xunit;
using static Exclr8.Terminal.Tests.TestHelpers;

namespace Exclr8.Terminal.Tests;

/// <summary>
/// OSC 8 hyperlink encoding — text between "OSC 8 ; ; URL ST" and
/// "OSC 8 ; ; ST" carries a link id resolvable via TryGetHyperlink.
/// </summary>
public class BufferOscHyperlinkTests
{
    [Fact]
    public void Osc8_TaggedCellsCarryLinkId()
    {
        var buf = NewBuffer(20, 2);
        buf.Feed(OSC + "8;;https://example.com" + ST + "click" + OSC + "8;;" + ST + "X");
        var row = buf.GetVisibleRow(0);
        Assert.NotEqual(0, row[0].HyperlinkId);
        Assert.Equal(row[0].HyperlinkId, row[4].HyperlinkId); // all 5 chars share id
        Assert.Equal(0, row[5].HyperlinkId); // the X is outside
    }

    [Fact]
    public void Osc8_UrlResolvable()
    {
        var buf = NewBuffer(20, 2);
        buf.Feed(OSC + "8;;https://example.com" + ST + "X" + OSC + "8;;" + ST);
        var row = buf.GetVisibleRow(0);
        Assert.True(buf.TryGetHyperlink(row[0].HyperlinkId, out var url));
        Assert.Equal("https://example.com", url);
    }

    [Fact]
    public void Osc8_EmptyUrlClosesActiveLink()
    {
        var buf = NewBuffer(20, 2);
        buf.Feed(OSC + "8;;https://a" + ST + "A" + OSC + "8;;" + ST + "B");
        var row = buf.GetVisibleRow(0);
        Assert.NotEqual(0, row[0].HyperlinkId);
        Assert.Equal(0, row[1].HyperlinkId);
    }
}
