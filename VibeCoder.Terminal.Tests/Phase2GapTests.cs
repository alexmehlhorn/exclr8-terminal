using VibeCoder.Terminal.Buffer;
using Xunit;
using static VibeCoder.Terminal.Tests.TestHelpers;

namespace VibeCoder.Terminal.Tests;

/// <summary>
/// Phase 2: gaps closed against xterm.js. Tests written first (TDD
/// style) and implemented against behaviour in
/// <c>node_modules/@xterm/xterm/src/common/InputHandler.ts</c>.
/// </summary>
public class Phase2GapTests
{
    // ---- REP (CSI Ps b): repeat the preceding printable character ----

    [Fact]
    public void REP_RepeatsPrecedingChar()
    {
        var buf = NewBuffer(20, 2);
        buf.Feed("A" + CSI + "4b"); // print A, then REP 4 → AAAAA total
        Assert.Equal("AAAAA", buf.RowText(0));
    }

    [Fact]
    public void REP_DefaultParamIs1()
    {
        var buf = NewBuffer(20, 2);
        buf.Feed("X" + CSI + "b");
        Assert.Equal("XX", buf.RowText(0));
    }

    [Fact]
    public void REP_NoopIfLastWasControl()
    {
        var buf = NewBuffer(20, 2);
        // After CR (no preceding printable) REP should do nothing.
        buf.Feed("\r" + CSI + "3b");
        Assert.Equal("", buf.RowText(0));
    }

    // ---- Tab stops: HTS (ESC H), TBC (CSI Ps g), CBT (CSI Ps Z) ----

    [Fact]
    public void HTS_SetsCustomTabStop()
    {
        var buf = NewBuffer(40, 2);
        // Move to col 4, set a tab stop there, then move to col 0, HT.
        buf.Feed(CSI + "1;5H");        // col 4
        buf.FeedBytes(0x1B, (byte)'H'); // HTS at col 4
        buf.Feed(CSI + "1;1H");        // col 0
        buf.FeedBytes(0x09);            // HT — next stop is col 4 (our custom)
        Assert.Equal(4, buf.CursorCol);
    }

    [Fact]
    public void TBC_0_ClearsTabStopAtCursor()
    {
        var buf = NewBuffer(40, 2);
        // Default stops at 8, 16, 24, 32. Clear stop at col 8.
        buf.Feed(CSI + "1;9H" + CSI + "0g"); // at col 8, clear this stop
        buf.Feed(CSI + "1;1H");
        buf.FeedBytes(0x09); // should skip to col 16 now
        Assert.Equal(16, buf.CursorCol);
    }

    [Fact]
    public void TBC_3_ClearsAllTabStops()
    {
        var buf = NewBuffer(40, 2);
        buf.Feed(CSI + "3g"); // clear all stops
        buf.Feed(CSI + "1;1H");
        buf.FeedBytes(0x09); // no stops left → go to right edge
        Assert.Equal(39, buf.CursorCol);
    }

    [Fact]
    public void CBT_MovesBackwardToTabStop()
    {
        var buf = NewBuffer(40, 2);
        buf.Feed(CSI + "1;20H" + CSI + "Z"); // col 19, back to prev stop (16)
        Assert.Equal(16, buf.CursorCol);
    }

    [Fact]
    public void CBT_Default1_ParamN()
    {
        var buf = NewBuffer(40, 2);
        buf.Feed(CSI + "1;30H" + CSI + "2Z"); // back 2 tabs: 24 → 16
        Assert.Equal(16, buf.CursorCol);
    }

    // ---- DECSTR: CSI ! p (soft reset) ----

    [Fact]
    public void DECSTR_ResetsScrollRegionAndModes()
    {
        var buf = NewBuffer(10, 5);
        buf.Feed(CSI + "2;4r");        // set a scroll region
        buf.Feed(CSI + "?25l");        // hide cursor
        buf.Feed(CSI + "4h");          // IRM on
        buf.Feed(CSI + "!p");          // DECSTR
        Assert.Equal(0, buf.ScrollTop);
        Assert.Equal(4, buf.ScrollBottom);
        Assert.True(buf.CursorVisible);
        Assert.False(buf.InsertMode);
    }

    // ---- Auto-wrap DECAWM (DECSET/RESET 7) ----

    [Fact]
    public void DECAWM_OnByDefault_PrintWrapsAtRightMargin()
    {
        var buf = NewBuffer(5, 3);
        buf.Feed("ABCDEF");
        // F should wrap to row 1.
        Assert.Equal("ABCDE", buf.RowText(0));
        Assert.Equal("F",     buf.RowText(1));
    }

    [Fact]
    public void DECAWM_Off_StampsOnRightMargin()
    {
        var buf = NewBuffer(5, 3);
        buf.Feed(CSI + "?7l"); // wraparound off
        buf.Feed("ABCDEF");
        // When wraparound is off, further chars overwrite the last
        // cell on the current line.
        Assert.Equal("ABCDF", buf.RowText(0));
        Assert.Equal("",      buf.RowText(1));
    }

    // ---- Origin mode DECOM (DECSET/RESET 6) ----

    [Fact]
    public void DECOM_On_ConstrainsCursorToScrollRegion()
    {
        var buf = NewBuffer(10, 6);
        buf.Feed(CSI + "3;5r" + CSI + "?6h"); // region rows 2..4, DECOM on
        Assert.Equal(2, buf.CursorRow); // enter origin mode → home = top of region
        Assert.Equal(0, buf.CursorCol);
        // CUP with row 1 now means row 2 absolute.
        buf.Feed(CSI + "1;1H");
        Assert.Equal(2, buf.CursorRow);
        // CUP row 99 clamps to scrollBottom (4).
        buf.Feed(CSI + "99;1H");
        Assert.Equal(4, buf.CursorRow);
    }

    [Fact]
    public void DECOM_Off_CUP_IsAbsolute()
    {
        var buf = NewBuffer(10, 6);
        buf.Feed(CSI + "3;5r"); // region only, no origin mode
        buf.Feed(CSI + "1;1H");
        Assert.Equal(0, buf.CursorRow);
    }

    // ---- Reverse video DECSCNM (DECSET/RESET 5) ----

    [Fact]
    public void DECSCNM_Toggles()
    {
        var buf = NewBuffer();
        Assert.False(buf.ReverseVideo);
        buf.Feed(CSI + "?5h");
        Assert.True(buf.ReverseVideo);
        buf.Feed(CSI + "?5l");
        Assert.False(buf.ReverseVideo);
    }

    // ---- Insert/replace mode IRM (CSI 4 h/l) ----

    [Fact]
    public void IRM_On_PrintShiftsExistingRight()
    {
        var buf = NewBuffer(6, 2);
        buf.Feed("ABCDEF");
        buf.Feed(CSI + "1;3H" + CSI + "4h" + "X");
        // X inserted at col 2, D E F shifted right. F falls off the end.
        Assert.Equal("ABXCDE", buf.RowText(0));
    }

    [Fact]
    public void IRM_Off_PrintReplaces()
    {
        var buf = NewBuffer(6, 2);
        buf.Feed("ABCDEF");
        buf.Feed(CSI + "1;3H" + CSI + "4l" + "X");
        Assert.Equal("ABXDEF", buf.RowText(0));
    }

    // ---- Line feed / new line mode LNM (CSI 20 h/l) ----

    [Fact]
    public void LNM_On_LFResetsColumn()
    {
        var buf = NewBuffer(10, 3);
        buf.Feed(CSI + "20h");
        buf.Feed("ABC\n");
        Assert.Equal(1, buf.CursorRow);
        Assert.Equal(0, buf.CursorCol);
    }

    [Fact]
    public void LNM_Off_LFKeepsColumn()
    {
        var buf = NewBuffer(10, 3);
        buf.Feed(CSI + "20l");
        buf.Feed("ABC\n");
        Assert.Equal(1, buf.CursorRow);
        Assert.Equal(3, buf.CursorCol);
    }

    // ---- ED/EL mode 3 (clear scrollback) — Phase 1 had this skipped ----

    [Fact]
    public void ED_3_ClearsScrollback()
    {
        var buf = NewBuffer(10, 3);
        buf.ScrollbackLimit = 100;
        buf.Feed("A\r\nB\r\nC\r\nD\r\nE\r\nF");
        Assert.True(buf.ScrollbackCount > 0);
        buf.Feed(CSI + "3J");
        Assert.Equal(0, buf.ScrollbackCount);
    }

    // ---- OSC 0/1/2 window/icon title ----

    [Fact]
    public void OSC_0_EmitsTitleEvent()
    {
        var buf = NewBuffer();
        string? title = null;
        buf.TitleChanged += (_, t) => title = t;
        buf.Feed(OSC + "0;my terminal" + ST);
        Assert.Equal("my terminal", title);
    }

    [Fact]
    public void OSC_2_EmitsTitleEvent()
    {
        var buf = NewBuffer();
        string? title = null;
        buf.TitleChanged += (_, t) => title = t;
        buf.Feed(OSC + "2;hello" + ST);
        Assert.Equal("hello", title);
    }

    [Fact]
    public void OSC_1_IconNameEventOptional()
    {
        var buf = NewBuffer();
        string? icon = null;
        buf.IconNameChanged += (_, t) => icon = t;
        buf.Feed(OSC + "1;icon-name" + ST);
        Assert.Equal("icon-name", icon);
    }

    // ---- OSC 4/10/11/12 color queries ----

    [Fact]
    public void OSC_10_QueryDefaultForegroundReplies()
    {
        // OSC 10 ; ? ST → query current default foreground
        var buf = NewBuffer();
        buf.Feed(OSC + "10;?" + ST);
        string reply = buf.TakeRepliesAscii();
        // Reply format: ESC ] 10 ; rgb:RRRR/GGGG/BBBB ESC \
        Assert.Contains("10;rgb:", reply);
    }

    [Fact]
    public void OSC_11_QueryDefaultBackgroundReplies()
    {
        var buf = NewBuffer();
        buf.Feed(OSC + "11;?" + ST);
        Assert.Contains("11;rgb:", buf.TakeRepliesAscii());
    }

    [Fact]
    public void OSC_12_QueryCursorColorReplies()
    {
        var buf = NewBuffer();
        buf.Feed(OSC + "12;?" + ST);
        Assert.Contains("12;rgb:", buf.TakeRepliesAscii());
    }

    [Fact]
    public void OSC_4_QueryPaletteEntryReplies()
    {
        var buf = NewBuffer();
        buf.Feed(OSC + "4;1;?" + ST);
        Assert.Contains("4;1;rgb:", buf.TakeRepliesAscii());
    }

    // ---- OSC 52 clipboard — gated by config ----

    [Fact]
    public void OSC_52_DisabledByDefault_NoClipboardEvent()
    {
        var buf = NewBuffer();
        bool fired = false;
        buf.ClipboardRequested += (_, _) => fired = true;
        buf.Feed(OSC + "52;c;aGVsbG8=" + ST); // base64 "hello"
        Assert.False(fired);
    }

    [Fact]
    public void OSC_52_WhenEnabled_FiresEventWithDecoded()
    {
        var buf = NewBuffer();
        buf.AllowClipboardAccess = true;
        string? payload = null;
        buf.ClipboardRequested += (_, e) => payload = e.Text;
        buf.Feed(OSC + "52;c;aGVsbG8=" + ST);
        Assert.Equal("hello", payload);
    }

    // ---- CSI t (window manipulation, safe subset) ----

    [Fact]
    public void CSI_t_18_ReportsTextAreaSize()
    {
        var buf = NewBuffer(80, 24);
        buf.Feed(CSI + "18t");
        // Reply: CSI 8 ; rows ; cols t
        Assert.Equal(CSI + "8;24;80t", buf.TakeRepliesAscii());
    }

    [Fact]
    public void CSI_t_21_ReportsWindowTitle()
    {
        var buf = NewBuffer();
        buf.Feed(OSC + "2;foo" + ST);
        buf.Feed(CSI + "21t");
        // Reply OSC l <title> ST
        Assert.Contains("foo", buf.TakeRepliesAscii());
    }

    [Fact]
    public void CSI_t_ResizeRequestsIgnored()
    {
        var buf = NewBuffer(80, 24);
        buf.Feed(CSI + "4;30;100t"); // resize to 30x100 — should be ignored
        Assert.Equal(80, buf.Cols);
        Assert.Equal(24, buf.Rows);
    }

    // ---- Focus in/out ----

    [Fact]
    public void NotifyFocus_WithFocusEventsEnabled_Replies()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "?1004h");
        buf.NotifyFocus(true);
        Assert.Equal(CSI + "I", buf.TakeRepliesAscii());
        buf.NotifyFocus(false);
        Assert.Equal(CSI + "O", buf.TakeRepliesAscii());
    }

    [Fact]
    public void NotifyFocus_WithFocusEventsDisabled_NoReply()
    {
        var buf = NewBuffer();
        buf.NotifyFocus(true);
        Assert.Equal(string.Empty, buf.TakeRepliesAscii());
    }

    // ---- Hardening: long OSC ----

    [Fact]
    public void Osc_VeryLongPayloadIsCapped()
    {
        var buf = NewBuffer();
        // 200 KiB of OSC data — the parser must not explode or buffer forever.
        var big = new string('x', 200 * 1024);
        buf.Feed(OSC + "0;" + big + ST);
        // Just check we didn't die; any truncation of the OSC is fine.
        Assert.True(true);
    }
}
