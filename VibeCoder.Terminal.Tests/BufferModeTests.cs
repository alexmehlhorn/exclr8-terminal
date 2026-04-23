using Xunit;
using static VibeCoder.Terminal.Tests.TestHelpers;

namespace VibeCoder.Terminal.Tests;

/// <summary>
/// DEC private modes (CSI ? Ps h/l) — cursor visibility, app cursor/
/// keypad, mouse modes, alt-screen toggling, bracketed paste, focus
/// events.
/// </summary>
public class BufferModeTests
{
    [Fact]
    public void DECTCEM_25_TogglesCursorVisibility()
    {
        var buf = NewBuffer();
        Assert.True(buf.CursorVisible);
        buf.Feed(CSI + "?25l");
        Assert.False(buf.CursorVisible);
        buf.Feed(CSI + "?25h");
        Assert.True(buf.CursorVisible);
    }

    [Fact]
    public void DECCKM_1_TogglesAppCursorKeys()
    {
        var buf = NewBuffer();
        Assert.False(buf.ApplicationCursorKeys);
        buf.Feed(CSI + "?1h");
        Assert.True(buf.ApplicationCursorKeys);
        buf.Feed(CSI + "?1l");
        Assert.False(buf.ApplicationCursorKeys);
    }

    [Fact]
    public void BracketedPaste_2004_Toggles()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "?2004h");
        Assert.True(buf.BracketedPaste);
        buf.Feed(CSI + "?2004l");
        Assert.False(buf.BracketedPaste);
    }

    [Fact]
    public void MouseMode1000_Set()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "?1000h");
        Assert.Equal(1000, buf.MouseMode);
        buf.Feed(CSI + "?1000l");
        Assert.Equal(0, buf.MouseMode);
    }

    [Fact]
    public void MouseMode1002And1003_Set()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "?1002h");
        Assert.Equal(1002, buf.MouseMode);
        buf.Feed(CSI + "?1003h");
        Assert.Equal(1003, buf.MouseMode);
    }

    [Fact]
    public void FocusEvents_1004_Toggles()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "?1004h");
        Assert.True(buf.FocusEvents);
        buf.Feed(CSI + "?1004l");
        Assert.False(buf.FocusEvents);
    }

    [Fact]
    public void AltScreen_1049_EntersAndExits()
    {
        var buf = NewBuffer(20, 3);
        buf.Feed("primary");
        buf.Feed(CSI + "?1049h");
        Assert.True(buf.IsAltScreen);
        // Alt screen should be blank on row 0.
        Assert.Equal("", buf.RowText(0));
        // Home the cursor so our ALT write lands at the top.
        buf.Feed(CSI + "H" + "ALT");
        Assert.Equal("ALT", buf.RowText(0));
        buf.Feed(CSI + "?1049l");
        Assert.False(buf.IsAltScreen);
        // Primary preserved.
        Assert.Equal("primary", buf.RowText(0));
    }

    [Fact]
    public void AltScreen_47_EntersWithoutSavingCursor()
    {
        var buf = NewBuffer();
        buf.Feed("text" + CSI + "?47h");
        Assert.True(buf.IsAltScreen);
    }

    [Fact]
    public void DECKPAM_EnablesAppKeypad()
    {
        var buf = NewBuffer();
        Assert.False(buf.ApplicationKeypad);
        buf.FeedBytes(0x1B, (byte)'=');
        Assert.True(buf.ApplicationKeypad);
        buf.FeedBytes(0x1B, (byte)'>');
        Assert.False(buf.ApplicationKeypad);
    }

    [Fact]
    public void DECSCUSR_SetsCursorStyles()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "1 q");
        Assert.Equal(Render.CursorStyle.BlockBlink, buf.CursorStyle);
        buf.Feed(CSI + "2 q");
        Assert.Equal(Render.CursorStyle.Block, buf.CursorStyle);
        buf.Feed(CSI + "4 q");
        Assert.Equal(Render.CursorStyle.Underline, buf.CursorStyle);
        buf.Feed(CSI + "6 q");
        Assert.Equal(Render.CursorStyle.Bar, buf.CursorStyle);
    }
}
