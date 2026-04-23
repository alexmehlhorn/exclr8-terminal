using VibeCoder.Terminal.Buffer;
using Xunit;
using static VibeCoder.Terminal.Tests.TestHelpers;

namespace VibeCoder.Terminal.Tests;

/// <summary>
/// Phase 4: semantics the renderer reads. We don't drive the Avalonia
/// path here (no display in CI) — we assert the buffer carries the
/// flags the renderer branches on.
/// </summary>
public class Phase4RenderTests
{
    [Fact]
    public void SGR_5_SetsBlinkFlag()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "5mX");
        Assert.True((buf.GetVisibleRow(0)[0].Flags2 & CellFlags2.Blink) != 0);
    }

    [Fact]
    public void SGR_6_AlsoSetsBlinkFlag()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "6mX");
        Assert.True((buf.GetVisibleRow(0)[0].Flags2 & CellFlags2.Blink) != 0);
    }

    [Fact]
    public void SGR_25_ClearsBlinkFlag()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "5mA" + CSI + "25mB");
        var row = buf.GetVisibleRow(0);
        Assert.True ((row[0].Flags2 & CellFlags2.Blink) != 0);
        Assert.False((row[1].Flags2 & CellFlags2.Blink) != 0);
    }

    // ---- DEC special graphics translation spot-checks matching
    // xterm.js's Charsets.ts entry "0" ----

    [Theory]
    [InlineData('`', 0x25C6)]
    [InlineData('a', 0x2592)]
    [InlineData('j', 0x2518)]
    [InlineData('k', 0x2510)]
    [InlineData('l', 0x250C)]
    [InlineData('m', 0x2514)]
    [InlineData('n', 0x253C)]
    [InlineData('q', 0x2500)]
    [InlineData('t', 0x251C)]
    [InlineData('u', 0x2524)]
    [InlineData('v', 0x2534)]
    [InlineData('w', 0x252C)]
    [InlineData('x', 0x2502)]
    [InlineData('y', 0x2264)]
    [InlineData('{', 0x03C0)]
    [InlineData('}', 0x00A3)]
    [InlineData('~', 0x00B7)]
    public void DecGraphics_EachMappedCodepoint(char ascii, int expected)
    {
        var buf = NewBuffer(10, 2);
        buf.FeedBytes(0x1B, (byte)'(', (byte)'0');
        buf.Feed(ascii.ToString());
        Assert.Equal(expected, buf.GetVisibleRow(0)[0].Rune);
    }
}
