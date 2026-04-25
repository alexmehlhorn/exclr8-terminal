using System;
using System.Linq;
using Exclr8.Terminal.Buffer;
using Exclr8.Terminal.Parser;
using Xunit;
using static Exclr8.Terminal.Tests.TestHelpers;

namespace Exclr8.Terminal.Tests;

/// <summary>
/// Smoke tests for the xterm.js parity pass: DECRQM/DA2/DA3 replies,
/// OSC 7 / OSC 133, synchronized output, parser extensibility, markers,
/// serialize, modifyOtherKeys, mouse encoding modes.
/// </summary>
public class NewFeatureTests
{
    [Fact]
    public void DA2_ReportsXtermSecondaryAttributes()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + ">c");
        Assert.Equal("\x1b[>0;276;0c", buf.TakeRepliesAscii());
    }

    [Fact]
    public void DA3_ReportsTertiaryAttributes()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "=c");
        Assert.StartsWith("\x1bP!|", buf.TakeRepliesAscii());
    }

    [Fact]
    public void DECRQM_AnsiInsertMode_ReportsState()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "4h");          // SM IRM = on
        buf.Feed(CSI + "4$p");         // DECRQM
        // Reply form: CSI <mode>;<status>$y. Status 1 = set.
        Assert.Equal("\x1b[4;1$y", buf.TakeRepliesAscii());
    }

    [Fact]
    public void DECRQM_DecBracketedPaste_ReportsState()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "?2004h");
        buf.Feed(CSI + "?2004$p");
        Assert.Equal("\x1b[?2004;1$y", buf.TakeRepliesAscii());
    }

    [Fact]
    public void DECRQSS_DECSTBM_ReportsCurrentRegion()
    {
        var buf = NewBuffer(80, 24);
        buf.Feed(CSI + "5;15r");        // DECSTBM rows 5..15
        buf.Feed("\x1bP$qr\x1b\\");    // DECRQSS for r
        Assert.Contains("$r5;15r", buf.TakeRepliesAscii());
    }

    [Fact]
    public void DECRQSS_Unknown_ReportsZero()
    {
        var buf = NewBuffer();
        buf.Feed("\x1bP$qx\x1b\\");
        Assert.Contains("0$rx", buf.TakeRepliesAscii());
    }

    [Fact]
    public void Osc7_FileScheme_StripsHostAndPercentDecodes()
    {
        var buf = NewBuffer();
        string? cwd = null;
        buf.WorkingDirectoryChanged += (_, p) => cwd = p;
        buf.Feed(OSC + "7;file://host/Users/ahm/some%20dir" + ST);
        Assert.Equal("/Users/ahm/some dir", cwd);
        Assert.Equal("/Users/ahm/some dir", buf.WorkingDirectory);
    }

    [Fact]
    public void Osc133_PromptStartAndCommandEnd_FireSemanticEvents()
    {
        var buf = NewBuffer();
        var kinds = new System.Collections.Generic.List<SemanticPromptKind>();
        int? exit = null;
        buf.SemanticPrompt += (_, e) => { kinds.Add(e.Kind); exit ??= e.ExitCode; };
        buf.Feed(OSC + "133;A" + ST);
        buf.Feed(OSC + "133;C" + ST);
        buf.Feed(OSC + "133;D;42" + ST);
        Assert.Equal(new[]
        {
            SemanticPromptKind.PromptStart,
            SemanticPromptKind.CommandStart,
            SemanticPromptKind.CommandEnd,
        }, kinds);
        Assert.Equal(42, exit);
    }

    [Fact]
    public void DECSET2026_RaisesSynchronizedOutputEvent()
    {
        var buf = NewBuffer();
        bool? lastState = null;
        int eventCount = 0;
        buf.SynchronizedOutputChanged += (_, on) => { lastState = on; eventCount++; };
        buf.Feed(CSI + "?2026h");
        Assert.True(buf.SynchronizedOutput);
        Assert.Equal(true, lastState);
        Assert.Equal(1, eventCount);
        buf.Feed(CSI + "?2026l");
        Assert.False(buf.SynchronizedOutput);
        Assert.Equal(false, lastState);
        Assert.Equal(2, eventCount);
    }

    [Fact]
    public void RegisterCsiHandler_InterceptsBeforeBuiltin()
    {
        var buf = NewBuffer();
        bool fired = false;
        using var _ = buf.RegisterCsiHandler('q', '?', (_, _) => { fired = true; return true; });
        buf.Feed(CSI + "?1q"); // bogus CSI ?1q
        Assert.True(fired);
    }

    [Fact]
    public void RegisterOscHandler_ClaimsId()
    {
        var buf = NewBuffer();
        string? captured = null;
        using var _ = buf.RegisterOscHandler(99, data => { captured = new string(data); return true; });
        buf.Feed(OSC + "99;hello world" + ST);
        Assert.Equal("hello world", captured);
    }

    [Fact]
    public void RegisterDcsHandler_ReceivesPayload()
    {
        var buf = NewBuffer();
        string? payload = null;
        using var _ = buf.RegisterDcsHandler('X', "", (_, _, p) => { payload = new string(p); return true; });
        buf.Feed("\x1bPX hello \x1b\\");
        Assert.Equal(" hello ", payload);
    }

    [Fact]
    public void Marker_StaysAnchoredAcrossScrollIntoScrollback()
    {
        var buf = NewBuffer(20, 3);
        buf.ScrollbackLimit = 100;
        buf.Feed("first\r\nsecond\r\nthird");
        var m = buf.RegisterMarker(); // anchored to "third" at abs row 2
        int initial = m.Line;
        buf.Feed("\r\n"); // "first" enters scrollback; "third" shifts up but abs unchanged
        Assert.True(m.IsValid);
        Assert.Equal(initial, m.Line); // abs is stable when scrollback grows below the anchor
    }

    [Fact]
    public void Marker_InvalidatesWhenContentEvicted()
    {
        var buf = NewBuffer(20, 3);
        buf.ScrollbackLimit = 2; // very small ring so eviction happens fast
        buf.Feed("first");
        var m = buf.RegisterMarker(); // anchored to "first"
        // Push enough content into scrollback that "first" evicts.
        for (int i = 0; i < 6; i++) buf.Feed("\r\nx");
        Assert.False(m.IsValid);
        Assert.Equal(-1, m.Line);
    }

    [Fact]
    public void Decoration_DisposesWhenMarkerDisposes()
    {
        var buf = NewBuffer();
        var m = buf.RegisterMarker();
        var d = buf.RegisterDecoration(new DecorationOptions { Marker = m, X = 0, Width = 5 });
        Assert.False(d.IsDisposed);
        m.Dispose();
        Assert.True(d.IsDisposed);
    }

    [Fact]
    public void Serialize_RoundTripsContent()
    {
        var buf = NewBuffer(10, 3);
        buf.Feed("hello\r\nworld");
        string s = buf.Serialize();
        Assert.Contains("hello", s);
        Assert.Contains("world", s);
        // Ends with cursor-position CSI.
        Assert.Contains("\x1b[", s);
    }

    [Fact]
    public void ModifyOtherKeys_Level2_IsTrackedByBuffer()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + ">4;2m");
        Assert.Equal(2, buf.ModifyOtherKeys);
        buf.Feed(CSI + ">4;0m");
        Assert.Equal(0, buf.ModifyOtherKeys);
    }

    [Fact]
    public void DECSET1006_SwitchesMouseEncoding()
    {
        var buf = NewBuffer();
        Assert.Equal(MouseEncoding.Default, buf.MouseEncoding);
        buf.Feed(CSI + "?1006h");
        Assert.Equal(MouseEncoding.Sgr, buf.MouseEncoding);
        buf.Feed(CSI + "?1016h");
        Assert.Equal(MouseEncoding.SgrPixels, buf.MouseEncoding);
        buf.Feed(CSI + "?1006l");
        Assert.Equal(MouseEncoding.Default, buf.MouseEncoding);
    }

    [Fact]
    public void HPA_VPR_SetCursorPosition()
    {
        var buf = NewBuffer(20, 10);
        buf.Feed(CSI + "8;1H");           // CUP row 8 col 1
        buf.Feed(CSI + "12`");             // HPA col 12 → cursor.col = 11
        Assert.Equal(11, buf.CursorCol);
        buf.Feed(CSI + "3e");              // VPR +3 → row 7+3=10, clamped to 9
        Assert.True(buf.CursorRow <= 9);
    }

    [Fact]
    public void DECIC_InsertsBlankColumns()
    {
        var buf = NewBuffer(10, 3);
        buf.Feed("abcdef");                // row 0 = "abcdef"
        buf.Feed(CSI + "3;3H");            // cursor at row 3 col 3 → row 2, col 2
        buf.Feed(CSI + "3;1H");            // row 3 col 1 → row 2, col 0
        buf.Feed(CSI + "1;3H");            // row 0, col 2 (so 'c')
        buf.Feed(CSI + "2'}");             // DECIC 2
        // Two blank columns inserted at col 2: "ab  cdef"
        Assert.Equal("ab  cdef", buf.RowText(0).TrimEnd());
    }

    [Fact]
    public void SLSR_ScrollLeftRight_MovesCells()
    {
        var buf = NewBuffer(10, 3);
        buf.Feed("abcdefghij");
        buf.Feed(CSI + "2 @"); // SL by 2 → row becomes "cdefghij  "
        Assert.Equal("cdefghij", buf.RowText(0).TrimEnd());
        buf.Feed(CSI + "1;1H");
        buf.Feed(CSI + "3 A"); // SR by 3 → "cdefghij  " becomes "   cdefghi" (last 2 lost)
        Assert.Equal("   cdefghi", buf.RowText(0).TrimEnd());
    }

    [Fact]
    public void SGR_4_3_SetsCurlyUnderlineSubparam()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "4:3m");
        buf.Feed("x");
        var cell = buf.GetVisibleRow(0)[0];
        Assert.True((cell.Flags & CellFlags.Underline) != 0);
        Assert.Equal(UnderlineStyle.Curly, cell.UnderlineStyle);
    }

    [Fact]
    public void SGR_58_SetsUnderlineColor()
    {
        var buf = NewBuffer();
        buf.Feed(CSI + "4;58;2;255;0;0m"); // underline + red underline color
        buf.Feed("x");
        var cell = buf.GetVisibleRow(0)[0];
        Assert.True((cell.Flags2 & CellFlags2.UlColorSet) != 0);
        Assert.Equal(0xFF0000u, cell.UnderlineRgb);
    }

    [Fact]
    public void VS16_RetroWidensNarrowEmoji()
    {
        var buf = NewBuffer(10, 3);
        // ⚠ U+26A0 is narrow by default. ⚠ + VS16 should be wide.
        buf.Feed("⚠️");
        var row = buf.GetVisibleRow(0);
        Assert.True((row[0].Flags2 & CellFlags2.IsWide) != 0);
        Assert.True((row[1].Flags2 & CellFlags2.IsContinuation) != 0);
    }

    [Fact]
    public void CombiningMark_DoesNotConsumeColumn()
    {
        var buf = NewBuffer(10, 3);
        // 'e' + combining acute U+0301 → still 1 column wide.
        buf.Feed("éf");
        Assert.Equal(2, buf.CursorCol);
    }
}
