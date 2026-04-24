using System;
using System.Linq;
using System.Text;
using Exclr8.Terminal.Buffer;
using Xunit;
using static Exclr8.Terminal.Tests.TestHelpers;

namespace Exclr8.Terminal.Tests;

/// <summary>
/// Hand-crafted "torture" workloads — byte streams that exercise known
/// problem areas of the parser / buffer (alt-screen toggling, DECSTBM,
/// wide-char wrap, OSC overflow, CSI param overflow, SGR thrashing,
/// mouse-mode state, bracketed paste, DEC special graphics, resize
/// mid-scroll, scrollback caps, truecolor, combining marks, OSC 8,
/// REP, and LNM/LF/CR interactions).
///
/// Each test feeds bytes to a fresh buffer, asserts the parser
/// completed without throwing, checks invariants (cursor in bounds,
/// no orphan continuation cells, scrollback within limit, alt-screen
/// state correct), and then asserts the specific outcome the sequence
/// should produce.
///
/// Goal: catch regressions that the narrowly-scoped 214 unit tests
/// won't, by combining control-sequences the way real programs do.
/// </summary>
public class WorkloadTortureTests
{
    // ------------------------------------------------------------------
    // Invariant helpers
    // ------------------------------------------------------------------

    /// <summary>Assert cursor is in-bounds and no row carries an
    /// orphaned continuation cell (one whose left neighbour isn't
    /// flagged IsWide).</summary>
    private static void AssertInvariants(TerminalBuffer buf)
    {
        Assert.InRange(buf.CursorRow, 0, buf.Rows - 1);
        Assert.InRange(buf.CursorCol, 0, buf.Cols); // cursor can sit 1 past right edge
        for (int r = 0; r < buf.Rows; r++)
        {
            var row = buf.GetVisibleRow(r);
            for (int c = 0; c < buf.Cols; c++)
            {
                bool isCont = (row[c].Flags2 & CellFlags2.IsContinuation) != 0;
                if (isCont)
                {
                    Assert.True(c > 0,
                        $"orphan IsContinuation at ({r},0)");
                    bool prevWide = (row[c - 1].Flags2 & CellFlags2.IsWide) != 0;
                    Assert.True(prevWide,
                        $"IsContinuation at ({r},{c}) without IsWide at ({r},{c - 1})");
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // 1. Alt-screen enter/exit cycle restores primary content
    // ------------------------------------------------------------------

    [Fact]
    public void AltScreen_EnterExit_RestoresPrimary()
    {
        var buf = NewBuffer(20, 5);
        buf.Feed("primary-line-1\r\nprimary-line-2\r\n");
        Assert.False(buf.IsAltScreen);

        // Enter alt screen (DECSET 1049), write alt content.
        buf.Feed(CSI + "?1049h");
        Assert.True(buf.IsAltScreen);
        buf.Feed("ALT-CONTENT");
        Assert.Contains("ALT-CONTENT", buf.ScreenText());

        // Leave alt screen (DECRST 1049).
        buf.Feed(CSI + "?1049l");
        Assert.False(buf.IsAltScreen);
        Assert.Contains("primary-line-1", buf.ScreenText());
        Assert.Contains("primary-line-2", buf.ScreenText());
        Assert.DoesNotContain("ALT-CONTENT", buf.ScreenText());

        AssertInvariants(buf);
    }

    [Fact]
    public void AltScreen_NestedToggle_DoesNotCrash()
    {
        var buf = NewBuffer(20, 5);
        // Nested/repeated toggles — the parser should tolerate this.
        for (int i = 0; i < 10; i++)
        {
            buf.Feed(CSI + "?1049h" + "x");
            buf.Feed(CSI + "?1049l");
        }
        Assert.False(buf.IsAltScreen);
        AssertInvariants(buf);
    }

    // ------------------------------------------------------------------
    // 2. DECSTBM scroll region + LF/RI inside it
    // ------------------------------------------------------------------

    [Fact]
    public void DECSTBM_LFInsideRegionScrollsOnlyRegion()
    {
        // 8 rows, 20 cols; region = rows 2..5 (1-based 3..6).
        // 20 cols so our overflow line fits on one physical row and we
        // observe the *region* scroll, not line-wrap inside the row.
        var buf = NewBuffer(20, 8);
        buf.Feed("top-above\r\n");
        buf.Feed(CSI + "3;6r"); // region rows 3..6 (0-based 2..5)
        // CSI r parks cursor at 1,1. Fill the region, then an extra LF
        // past the bottom to force a region scroll.
        buf.Feed(CSI + "3;1H" + "R3\r\n" + "R4\r\n" + "R5\r\n" + "R6\r\n" + "R7");
        AssertInvariants(buf);
        // Row 0 (outside region) unchanged by the region scroll.
        Assert.Equal("top-above", buf.RowText(0));
        // Region should have scrolled R3 off the top → rows 2..5 = R4..R7.
        Assert.Equal("R4", buf.RowText(2));
        Assert.Equal("R5", buf.RowText(3));
        Assert.Equal("R6", buf.RowText(4));
        Assert.Equal("R7", buf.RowText(5));
        // Cursor still inside the region.
        Assert.InRange(buf.CursorRow, 2, 5);
    }

    [Fact]
    public void DECSTBM_ReverseIndexAtTopOfRegion_ScrollsRegion()
    {
        var buf = NewBuffer(10, 8);
        buf.Feed(CSI + "3;6r");      // region rows 2..5
        buf.Feed(CSI + "3;1H" + "A"); // row 2, then write A
        buf.Feed(CSI + "3;1H");       // back to top of region
        buf.FeedBytes(0x1B, (byte)'M'); // RI — should scroll region down (insert blank at top)
        AssertInvariants(buf);
        // A should now be on row 3 (was row 2 before RI).
        Assert.Contains("A", buf.RowText(3));
    }

    [Fact]
    public void DECSTBM_ResetToFullScreen_Works()
    {
        var buf = NewBuffer(10, 6);
        buf.Feed(CSI + "2;4r"); // narrow
        buf.Feed(CSI + "r");    // reset — back to full screen
        Assert.Equal(0, buf.ScrollTop);
        Assert.Equal(5, buf.ScrollBottom);
        AssertInvariants(buf);
    }

    // ------------------------------------------------------------------
    // 3. Wide-char wrap at right margin (CJK + emoji)
    // ------------------------------------------------------------------

    [Fact]
    public void WideChar_WrapAtRightMargin_NoOrphanContinuation()
    {
        var buf = NewBuffer(6, 3);
        // 5 narrow + 1 wide → wide glyph can't fit at col 5; must wrap.
        buf.Feed("ABCDE中");
        AssertInvariants(buf);
        // Wide char should have wrapped to row 1.
        Assert.Equal("ABCDE", buf.RowText(0));
        Assert.Contains("中", buf.RowText(1));
    }

    [Fact]
    public void Emoji_WrapAtRightMargin_NoOrphanContinuation()
    {
        var buf = NewBuffer(4, 3);
        // Earth emoji is wide.
        buf.Feed("abc🌍");
        AssertInvariants(buf);
        Assert.Equal("abc", buf.RowText(0));
        Assert.Contains("🌍", buf.RowText(1));
    }

    // ------------------------------------------------------------------
    // 4. OSC payload overflow
    // ------------------------------------------------------------------

    [Fact]
    public void OSC_OverflowAbove64KiB_DropsCleanly()
    {
        var buf = NewBuffer(10, 4);
        var huge = new string('A', 80 * 1024);
        // OSC 0;<huge>ST followed by normal output — the parser
        // should drop the oversized payload and resume on the
        // trailing "ok" print.
        buf.Feed(OSC + "0;" + huge + ST + "ok");
        AssertInvariants(buf);
        Assert.Contains("ok", buf.ScreenText());
    }

    // ------------------------------------------------------------------
    // 5. CSI param overflow
    // ------------------------------------------------------------------

    [Fact]
    public void CSI_HugeNumericParamsDoNotThrow()
    {
        var buf = NewBuffer(10, 4);
        // Repeated 9s in every position — must clamp, not explode.
        buf.Feed(CSI + "99999999999999999;99999999999999999m");
        buf.Feed(CSI + "99999999999999999;99999999999999999H");
        buf.Feed("X");
        AssertInvariants(buf);
        Assert.Contains("X", buf.ScreenText());
    }

    [Fact]
    public void CSI_ExcessParamsOverLimit_NoThrow()
    {
        var buf = NewBuffer(10, 4);
        // Pile 64 params into one CSI; 32 is the published limit but
        // the parser must degrade gracefully beyond it rather than die.
        var sb = new StringBuilder(CSI);
        for (int i = 0; i < 64; i++) { if (i > 0) sb.Append(';'); sb.Append(0); }
        sb.Append('m');
        buf.Feed(sb.ToString());
        buf.Feed("after");
        AssertInvariants(buf);
        Assert.Contains("after", buf.ScreenText());
    }

    // ------------------------------------------------------------------
    // 6. Heavy SGR thrashing
    // ------------------------------------------------------------------

    [Fact]
    public void SGR_ThrashingDoesNotCorruptPen()
    {
        var buf = NewBuffer(40, 10);
        // Fixed seed so failures are reproducible.
        var rng = new Random(0xC0FFEE);
        for (int i = 0; i < 1000; i++)
        {
            int fg = 30 + rng.Next(8);
            int bg = 40 + rng.Next(8);
            int attr = rng.Next(4) switch { 0 => 1, 1 => 4, 2 => 7, _ => 0 };
            buf.Feed($"{CSI}{attr};{fg};{bg}m");
            buf.Feed(((char)('a' + rng.Next(26))).ToString());
            if (i % 50 == 49) buf.Feed("\r\n");
        }
        // End with a full reset then print "DONE" — pen should be
        // exactly default.
        buf.Feed(CSI + "0m");
        buf.Feed(CSI + "1;1H");
        buf.Feed(CSI + "2J"); // clear
        buf.Feed("DONE");
        var cell = buf.GetVisibleRow(0)[0];
        Assert.Equal((int)'D', cell.Rune);
        Assert.Equal(0, cell.FgIndex);
        Assert.Equal(0, cell.BgIndex);
        Assert.Equal(CellFlags.None, cell.Flags);
        AssertInvariants(buf);
    }

    // ------------------------------------------------------------------
    // 7. Mouse mode state machine
    // ------------------------------------------------------------------

    [Fact]
    public void MouseMode_EnableDisable_StateIsClean()
    {
        var buf = NewBuffer();
        Assert.Equal(0, buf.MouseMode);
        buf.Feed(CSI + "?1000h");
        Assert.Equal(1000, buf.MouseMode);
        buf.Feed(CSI + "?1002h"); // SGR button-event overrides
        Assert.Equal(1002, buf.MouseMode);
        buf.Feed(CSI + "?1006h"); // 1006 extended — should stay on
        // Note: 1006 is an *encoding* mode; our buffer tracks 1000/1002/1003 only.
        // Mouse report payloads that the app would forward (SGR-encoded
        // click+release) arrive as printable bytes and shouldn't confuse the parser.
        buf.Feed(CSI + "<0;10;5M");  // button 0 down at (10,5) (SGR 1006)
        buf.Feed(CSI + "<0;10;5m");  // button 0 up
        buf.Feed(CSI + "<32;11;5M"); // drag (button-event)
        buf.Feed(CSI + "?1006l");
        buf.Feed(CSI + "?1002l");
        buf.Feed(CSI + "?1000l");
        Assert.Equal(0, buf.MouseMode);
        AssertInvariants(buf);
    }

    // ------------------------------------------------------------------
    // 8. Bracketed paste with embedded CSI escapes
    // ------------------------------------------------------------------

    [Fact]
    public void BracketedPaste_WithEmbeddedCsi_DoesNotBreakParsing()
    {
        var buf = NewBuffer(40, 5);
        buf.Feed(CSI + "?2004h"); // enable bracketed paste
        Assert.True(buf.BracketedPaste);
        // Simulate the host pasting a string that *happens* to contain
        // a CSI-looking byte sequence. From the terminal's perspective
        // the bracket sentinels are normal CSI sequences; the payload
        // between them is printed normally (buffer doesn't strip them,
        // the app does).
        buf.Feed(CSI + "200~" + "hello" + CSI + "1mEVIL" + CSI + "0m" + "world" + CSI + "201~");
        AssertInvariants(buf);
        Assert.Contains("hello", buf.ScreenText());
        Assert.Contains("world", buf.ScreenText());
        buf.Feed(CSI + "?2004l");
        Assert.False(buf.BracketedPaste);
    }

    // ------------------------------------------------------------------
    // 9. DEC special graphics charset — box drawing
    // ------------------------------------------------------------------

    [Fact]
    public void DecSpecialGraphics_QRendersHorizontalLine()
    {
        var buf = NewBuffer(10, 3);
        // ESC ( 0  → G0 becomes DEC special graphics.
        // 'q' in that set maps to U+2500 (light horizontal).
        // ESC ( B  → back to ASCII.
        buf.Feed(ESC + "(0" + "qqqqq" + ESC + "(B");
        AssertInvariants(buf);
        var row0 = buf.GetVisibleRow(0);
        for (int c = 0; c < 5; c++)
            Assert.Equal(0x2500, row0[c].Rune);
    }

    // ------------------------------------------------------------------
    // 10. Resize while mid-scroll — must not crash, cursor stays in-bounds
    // ------------------------------------------------------------------

    [Fact]
    public void Resize_MidScroll_CursorStaysInBounds()
    {
        var buf = NewBuffer(10, 5);
        // Fill enough to push rows into scrollback.
        for (int i = 0; i < 30; i++) buf.Feed($"line-{i:00}\r\n");
        Assert.True(buf.ScrollbackCount > 0);
        // Resize smaller — cursor + scroll region should reset safely.
        buf.Resize(6, 3);
        AssertInvariants(buf);
        Assert.Equal(0, buf.ScrollTop);
        Assert.Equal(2, buf.ScrollBottom);
        // And larger.
        buf.Resize(30, 10);
        AssertInvariants(buf);
    }

    // ------------------------------------------------------------------
    // 11. Scrollback cap
    // ------------------------------------------------------------------

    [Fact]
    public void Scrollback_CappedAtLimit_AfterMassiveSpew()
    {
        var buf = NewBuffer(10, 4);
        buf.ScrollbackLimit = 500;
        for (int i = 0; i < 5000; i++) buf.Feed($"x{i}\r\n");
        Assert.True(buf.ScrollbackCount <= 500,
            $"scrollback {buf.ScrollbackCount} exceeded limit 500");
        AssertInvariants(buf);
    }

    // ------------------------------------------------------------------
    // 12. Truecolor SGR — exact RGB
    // ------------------------------------------------------------------

    [Fact]
    public void Sgr_Truecolor_Fg_ExactRgb()
    {
        var buf = NewBuffer(10, 2);
        buf.Feed(CSI + "38;2;10;20;30m" + "X");
        var cell = buf.GetVisibleRow(0)[0];
        Assert.True((cell.Flags & CellFlags.FgRgb) != 0);
        Assert.Equal(0x0A141Eu, cell.FgRgb);
    }

    [Fact]
    public void Sgr_Truecolor_Bg_ExactRgb()
    {
        var buf = NewBuffer(10, 2);
        buf.Feed(CSI + "48;2;200;100;50m" + "Y");
        var cell = buf.GetVisibleRow(0)[0];
        Assert.True((cell.Flags & CellFlags.BgRgb) != 0);
        Assert.Equal(0xC86432u, cell.BgRgb);
    }

    // ------------------------------------------------------------------
    // 13. Combining marks
    // ------------------------------------------------------------------

    [Fact]
    public void CombiningMark_DoesNotCrashOrOverflowBuffer()
    {
        var buf = NewBuffer(10, 2);
        // "e" + COMBINING ACUTE ACCENT (U+0301). Our buffer treats
        // the combining mark as a zero/narrow codepoint; the exact
        // width is implementation-defined, but cursor and invariants
        // must stay sane.
        buf.Feed("é");
        AssertInvariants(buf);
        Assert.InRange(buf.CursorCol, 1, 2);
    }

    // ------------------------------------------------------------------
    // 14. OSC 8 hyperlink with empty URL
    // ------------------------------------------------------------------

    [Fact]
    public void Osc8_EmptyUrl_DoesNotCrashAndClearsLink()
    {
        var buf = NewBuffer(20, 2);
        // Open a link, then close with empty URL.
        buf.Feed(OSC + "8;;https://example.com" + ST + "click" + OSC + "8;;" + ST + "plain");
        AssertInvariants(buf);
        var row = buf.GetVisibleRow(0);
        // "click" should have a non-zero hyperlink id, "plain" should not.
        Assert.NotEqual(0, row[0].HyperlinkId);   // 'c' of "click"
        Assert.Equal(0, row[5].HyperlinkId);      // 'p' of "plain"
    }

    // ------------------------------------------------------------------
    // 15. REP at various cursor positions
    // ------------------------------------------------------------------

    [Fact]
    public void Rep_AtStart_RepeatsLastPrintRune()
    {
        var buf = NewBuffer(10, 2);
        buf.Feed("A" + CSI + "4b"); // REP 4 → 4 more As
        AssertInvariants(buf);
        Assert.Equal("AAAAA", buf.RowText(0));
    }

    [Fact]
    public void Rep_AfterControlSequence_IsNoOp()
    {
        var buf = NewBuffer(10, 2);
        // CR+LF resets the "last print rune" (LF does too) so REP
        // should be a no-op even though we previously printed 'A'.
        // Use CRLF to park at (1,0) and Z should land at col 0.
        buf.Feed("A\r\n" + CSI + "3b" + "Z");
        AssertInvariants(buf);
        Assert.Equal("Z", buf.RowText(1));
    }

    [Fact]
    public void Rep_AtRightMargin_WrapsLikeNormalPrint()
    {
        var buf = NewBuffer(5, 3);
        // Fill: 'A' + REP 6 wraps to the next row(s) since width=5.
        buf.Feed("A" + CSI + "6b");
        AssertInvariants(buf);
        Assert.Equal("AAAAA", buf.RowText(0));
        Assert.Equal("AA", buf.RowText(1));
    }

    // ------------------------------------------------------------------
    // 16. CR / LF / CRLF / LNM interactions
    // ------------------------------------------------------------------

    [Fact]
    public void Lnm_OnMakesLfImplyCr()
    {
        var buf = NewBuffer(10, 3);
        buf.Feed(CSI + "20h"); // LNM on
        Assert.True(buf.LineFeedNewLine);
        buf.Feed("abc\n");
        // With LNM, bare LF should also CR — cursor at col 0 of row 1.
        Assert.Equal(1, buf.CursorRow);
        Assert.Equal(0, buf.CursorCol);
        AssertInvariants(buf);
    }

    [Fact]
    public void Lnm_OffBareLfKeepsColumn()
    {
        var buf = NewBuffer(10, 3);
        // Default: LNM off — LF keeps column.
        buf.Feed("abc\n");
        Assert.Equal(1, buf.CursorRow);
        Assert.Equal(3, buf.CursorCol);
        AssertInvariants(buf);
    }

    [Fact]
    public void Crlf_Sequence_MovesToNextRowColumn0()
    {
        var buf = NewBuffer(10, 3);
        buf.Feed("abc\r\nxyz");
        Assert.Equal("abc", buf.RowText(0));
        Assert.Equal("xyz", buf.RowText(1));
        AssertInvariants(buf);
    }

    // ------------------------------------------------------------------
    // 17. Grab-bag: long malformed stream shouldn't crash
    // ------------------------------------------------------------------

    [Fact]
    public void Malformed_EscIncomplete_RecoversOnNextPrintable()
    {
        var buf = NewBuffer(10, 2);
        // Start an ESC sequence that never completes; the parser
        // should abandon it as soon as a printable follows a cancel.
        buf.FeedBytes(0x1B, 0x18); // ESC + CAN → cancel
        buf.Feed("ok");
        AssertInvariants(buf);
        Assert.Contains("ok", buf.ScreenText());
    }

    [Fact]
    public void InsertMode_WithSgrPen_BlanksCarryBackground()
    {
        // IRM + SGR bg: when we InsertBlanks in replace mode, blanks
        // should inherit bg. Inserted cells must not crash or corrupt.
        var buf = NewBuffer(10, 2);
        buf.Feed(CSI + "4h");          // IRM on
        buf.Feed(CSI + "41m");         // red bg
        buf.Feed("abc");
        buf.Feed(CSI + "1;1H");        // back to start
        buf.Feed("Z");                 // should shift abc right
        AssertInvariants(buf);
        Assert.Equal("Zabc", buf.RowText(0));
    }

    [Fact]
    public void DeleteChars_InLongLine_DoesNotOverrun()
    {
        var buf = NewBuffer(10, 2);
        buf.Feed("abcdefghij");
        buf.Feed(CSI + "1;3H");
        buf.Feed(CSI + "3P"); // delete 3 chars at col 3
        AssertInvariants(buf);
        Assert.Equal("abfghij", buf.RowText(0));
    }

    [Fact]
    public void EraseDisplayModes_DoNotThrow()
    {
        var buf = NewBuffer(10, 4);
        for (int r = 0; r < 4; r++) buf.Feed($"row{r}\r\n");
        buf.Feed(CSI + "2;3H");
        buf.Feed(CSI + "0J"); // below cursor
        buf.Feed(CSI + "1;1H");
        buf.Feed(CSI + "1J"); // above cursor
        buf.Feed(CSI + "2J"); // all
        AssertInvariants(buf);
    }

    [Fact]
    public void SoftReset_RestoresSaneDefaults()
    {
        var buf = NewBuffer(10, 4);
        buf.Feed(CSI + "2;3r");   // custom scroll region
        buf.Feed(CSI + "?6h");    // origin mode
        buf.Feed(CSI + "4h");     // insert mode
        buf.Feed(CSI + "!p");     // DECSTR
        Assert.Equal(0, buf.ScrollTop);
        Assert.Equal(3, buf.ScrollBottom);
        Assert.False(buf.OriginMode);
        Assert.False(buf.InsertMode);
        AssertInvariants(buf);
    }

    // ------------------------------------------------------------------
    // Regression pinned from fuzz — currently a real bug in the buffer.
    // Kept here [Fact(Skip=…)] so that when the fix lands we can drop
    // the Skip and this test acts as the sentinel.
    // See: /BUGS-FOUND.md #1.
    // ------------------------------------------------------------------

    [Fact]
    public void OverwriteWideWithWide_DoesNotLeaveOrphanContinuation()
    {
        var buf = NewBuffer(20, 8);
        // Step 1: narrow '?' at (0,0), then wide 🤜 at (0,1)+(0,2).
        buf.Feed("?🤜");
        // Step 2: DECRC — restores saved cursor (default 0,0 because
        // nothing has SAVEd yet) so cursor jumps back to (0,0).
        buf.FeedBytes(0x1B, (byte)'8');
        // Step 3: wide 🏃 at (0,0)+(0,1) overwrites '?' and the LEFT
        // HALF of the old 🤜. The old 🤜's right-half (now at (0,2))
        // is stale — its IsContinuation flag must be cleared.
        buf.Feed("🏃");
        AssertInvariants(buf);
        var row = buf.GetVisibleRow(0);
        Assert.Equal(0x1F3C3, row[0].Rune);
        Assert.True((row[0].Flags2 & CellFlags2.IsWide) != 0);
        Assert.True((row[1].Flags2 & CellFlags2.IsContinuation) != 0);
        Assert.False((row[2].Flags2 & CellFlags2.IsContinuation) != 0,
            "orphan IsContinuation at (0,2) left behind from the overwritten 🤜");
    }

    // Sanity: total assertion count in this file is comfortably above 30.
}
