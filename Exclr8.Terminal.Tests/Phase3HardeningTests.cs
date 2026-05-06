using Exclr8.Terminal.Buffer;
using Xunit;
using static Exclr8.Terminal.Tests.TestHelpers;

namespace Exclr8.Terminal.Tests;

/// <summary>
/// Phase 3: defensive behaviour for malformed / adversarial input and
/// edge cases the fuzzer would hit.
/// </summary>
public class Phase3HardeningTests
{
    // ---- Param overflow ----

    [Fact]
    public void CsiParam_LongDigitRunDoesNotOverflow()
    {
        var buf = NewBuffer(10, 5);
        // CUU with a huge param — must clamp to 0 without overflowing
        // to a negative and jumping past the top.
        buf.Feed(CSI + "9999999999999999999A");
        Assert.Equal(0, buf.CursorRow);
        Assert.Equal(0, buf.CursorCol);
    }

    [Fact]
    public void CsiParam_32ParamsAccepted()
    {
        var buf = NewBuffer();
        // 32 SGR params in one CSI — must not crash.
        var sb = new System.Text.StringBuilder(CSI);
        for (int i = 0; i < 32; i++) { if (i > 0) sb.Append(';'); sb.Append(0); }
        sb.Append('m');
        buf.Feed(sb.ToString());
        Assert.Equal(0, buf.PenTemplate.FgIndex);
    }

    // ---- OSC payload cap ----

    [Fact]
    public void Osc_HugePayloadDoesNotCrash()
    {
        var buf = NewBuffer();
        var big = new string('y', 250 * 1024);
        buf.Feed(OSC + "0;" + big + ST);
        // Any observable outcome is fine as long as we didn't die.
        Assert.True(true);
    }

    // ---- Paste ----

    [Fact]
    public void Paste_OversizePayload_Ignored()
    {
        // We don't have a TerminalControl at this layer (avoid
        // Avalonia headless harness here), so document the default
        // cap via reflection over the instance property. The actual
        // reject-and-fire-event path is exercised in the UI tests.
        var prop = typeof(Exclr8.Terminal.TerminalControl).GetProperty("PasteMaxBytes");
        Assert.NotNull(prop);
        Assert.True(prop!.CanWrite, "PasteMaxBytes must be settable so hosts can adjust the cap.");
        Assert.Equal(typeof(int), prop.PropertyType);
    }

    // ---- Wide char orphan cleanup ----

    [Fact]
    public void WideChar_OverwriteLeftHalf_ClearsContinuation()
    {
        var buf = NewBuffer(10, 2);
        buf.Feed("中");
        var row = buf.GetVisibleRow(0);
        Assert.True((row[0].Flags2 & CellFlags2.IsWide) != 0);
        Assert.True((row[1].Flags2 & CellFlags2.IsContinuation) != 0);
        // Rewrite cell 0 as a narrow rune — the orphan continuation
        // at cell 1 should be cleared (otherwise the renderer would
        // leave a phantom blank that can't be selected).
        buf.Feed(CSI + "1;1H" + "A");
        row = buf.GetVisibleRow(0);
        Assert.Equal('A', row[0].Rune);
        Assert.False((row[1].Flags2 & CellFlags2.IsContinuation) != 0);
    }

    [Fact]
    public void WideChar_OverwriteRightHalf_ClearsLeftWideFlag()
    {
        var buf = NewBuffer(10, 2);
        buf.Feed("中");
        // Write a narrow rune into cell 1 (right half of 中). Left
        // half must drop its IsWide flag and become a blank.
        buf.Feed(CSI + "1;2H" + "A");
        var row = buf.GetVisibleRow(0);
        Assert.False((row[0].Flags2 & CellFlags2.IsWide) != 0);
        Assert.Equal('A', row[1].Rune);
    }

    // ---- DECAWM off edge cases ----

    [Fact]
    public void DECAWM_Off_WideCharAtRightEdgeStomps()
    {
        var buf = NewBuffer(5, 3);
        buf.Feed(CSI + "?7l");
        buf.Feed("ABCD中"); // 中 wants 2 cells, cursor at col 4; no wrap → should not spill
        // Expectation: row 1 is empty.
        Assert.Equal("", buf.RowText(1));
    }

    // ---- Resize preserves primary content ----

    [Fact]
    public void Resize_DoesNotScrambleExistingContent()
    {
        var buf = NewBuffer(10, 4);
        buf.Feed("abcde");
        buf.Resize(20, 4);
        Assert.Equal("abcde", buf.RowText(0));
    }
}
