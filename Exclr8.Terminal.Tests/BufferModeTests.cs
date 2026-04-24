using Xunit;
using static Exclr8.Terminal.Tests.TestHelpers;

namespace Exclr8.Terminal.Tests;

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
    public void AltScreen_1049_RestoresPrimaryCursorEvenAfterDECSCInsideAlt()
    {
        // Regression: DECSET 1049 must save the primary cursor to the
        // PRIMARY screen's DECSC slot (xterm semantics). If it saves
        // to the alt slot, DECSC/DECRC inside the alt screen would
        // overwrite the 1049 anchor and DECRESET 1049 would restore
        // the wrong cursor onto primary.
        var buf = NewBuffer(20, 8);

        // Park the primary cursor at (row 3, col 5). CUP is 1-based.
        buf.Feed(CSI + "4;6H");
        Assert.Equal(3, buf.CursorRow);
        Assert.Equal(5, buf.CursorCol);

        // Enter alt via 1049 — saves primary cursor.
        buf.Feed(CSI + "?1049h");
        Assert.True(buf.IsAltScreen);

        // Move somewhere on alt and do a DECSC/DECRC dance that writes
        // to the alt screen's DECSC slot.
        buf.Feed(CSI + "1;1H");   // (0,0)
        buf.FeedBytes(0x1B, (byte)'7'); // DECSC in alt
        buf.Feed(CSI + "5;10H");  // move away (4,9)
        buf.FeedBytes(0x1B, (byte)'8'); // DECRC — back to (0,0) via alt slot
        Assert.Equal(0, buf.CursorRow);
        Assert.Equal(0, buf.CursorCol);

        // Leave alt — must restore primary cursor to (3,5), not the
        // (0,0) that DECSC-inside-alt saved.
        buf.Feed(CSI + "?1049l");
        Assert.False(buf.IsAltScreen);
        Assert.Equal(3, buf.CursorRow);
        Assert.Equal(5, buf.CursorCol);
    }

    [Fact]
    public void AltScreen_1049_ReEntryWhileAlreadyOnAltIsNoOp()
    {
        // DECSET 1049 while already on alt shouldn't re-save (would
        // capture the alt cursor and corrupt the primary-restore).
        var buf = NewBuffer(20, 8);
        buf.Feed(CSI + "3;3H");           // primary cursor (2,2)
        buf.Feed(CSI + "?1049h");         // save primary, enter alt
        buf.Feed(CSI + "5;5H");           // alt cursor (4,4)
        buf.Feed(CSI + "?1049h");         // redundant — must not re-save
        buf.Feed(CSI + "?1049l");         // leave alt
        // Original primary cursor should be restored.
        Assert.Equal(2, buf.CursorRow);
        Assert.Equal(2, buf.CursorCol);
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
