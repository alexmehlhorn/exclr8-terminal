using System;
using System.IO;
using Exclr8.Terminal.Buffer;
using Xunit;
using static Exclr8.Terminal.Tests.TestHelpers;

namespace Exclr8.Terminal.Tests;

/// <summary>
/// Replay byte streams captured from real programs running under a
/// pty on the controller host (macOS). Fixtures live in
/// <c>Exclr8.Terminal.Tests/Fixtures/*.bin</c> and are copied to the
/// test output directory by the csproj.
///
/// For each capture we:
///   1. size a fresh buffer to the same geometry the capture was
///      recorded at (120×40 for TERM=xterm-256color, 80×24 for legacy),
///   2. feed the entire file in one shot,
///   3. assert no exception + invariants (cursor in-bounds, no orphan
///      continuation cells, scrollback within limit, alt-screen state
///      matches what the capture's last byte should have produced),
///   4. do a small number of content assertions that are stable
///      between runs (colour of a specific cell, non-empty screen,
///      presence of expected strings, etc.).
///
/// These are the sorts of regressions the synthetic torture tests
/// won't hit: a real program emits escape sequences in combinations
/// and orderings nobody hand-codes.
/// </summary>
public class WorkloadReplayTests
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Fixtures");

    private static byte[] Load(string name)
    {
        var path = Path.Combine(FixtureDir, name);
        Assert.True(File.Exists(path), $"fixture missing: {path}");
        return File.ReadAllBytes(path);
    }

    private static void AssertInvariants(TerminalBuffer buf)
    {
        Assert.InRange(buf.CursorRow, 0, buf.Rows - 1);
        Assert.InRange(buf.CursorCol, 0, buf.Cols);
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
        Assert.True(buf.ScrollbackCount <= buf.ScrollbackLimit);
    }

    // ------------------------------------------------------------------
    // 1. Truecolor: printf ESC[38;2;R;G;B] ... ESC[48;2;R;G;B] ...
    // ------------------------------------------------------------------

    [Fact]
    public void Replay_TrueColor_CarriesExactRgb()
    {
        var bytes = Load("truecolor.bin");
        var buf   = new TerminalBuffer(80, 24);
        buf.Write(bytes);
        AssertInvariants(buf);

        // First row opens SGR 38;2;255;100;50 and writes "HELLO".
        // Second row opens SGR 48;2;10;20;200 and writes "WORLD".
        var row0 = buf.GetVisibleRow(0);
        Assert.Equal('H', row0[0].Rune);
        Assert.True((row0[0].Flags & CellFlags.FgRgb) != 0);
        Assert.Equal(0xFF6432u, row0[0].FgRgb); // 255,100,50

        var row1 = buf.GetVisibleRow(1);
        Assert.Equal('W', row1[0].Rune);
        Assert.True((row1[0].Flags & CellFlags.BgRgb) != 0);
        Assert.Equal(0x0A14C8u, row1[0].BgRgb); // 10,20,200
    }

    // ------------------------------------------------------------------
    // 2. CJK + emoji
    // ------------------------------------------------------------------

    [Fact]
    public void Replay_CjkAndEmoji_RendersAsWideCells()
    {
        var bytes = Load("cjk.bin");
        var buf   = new TerminalBuffer(120, 40);
        buf.Write(bytes);
        AssertInvariants(buf);

        // Row 0: 你好世界 — four wide glyphs.
        var row0 = buf.GetVisibleRow(0);
        Assert.True((row0[0].Flags2 & CellFlags2.IsWide) != 0);
        Assert.True((row0[1].Flags2 & CellFlags2.IsContinuation) != 0);
        Assert.Equal(0x4F60, row0[0].Rune); // 你
        Assert.Equal(0x597D, row0[2].Rune); // 好
        Assert.Equal(0x4E16, row0[4].Rune); // 世
        Assert.Equal(0x754C, row0[6].Rune); // 界

        // Row 2: 🌍🎨 — two wide emoji.
        var row2 = buf.GetVisibleRow(2);
        Assert.True((row2[0].Flags2 & CellFlags2.IsWide) != 0);
        Assert.Equal(0x1F30D, row2[0].Rune); // 🌍
        Assert.Equal(0x1F3A8, row2[2].Rune); // 🎨
    }

    // ------------------------------------------------------------------
    // 3. ls with ANSI colour
    // ------------------------------------------------------------------

    [Fact]
    public void Replay_LsColor_RendersWithoutOrphans()
    {
        var bytes = Load("ls-color.bin");
        var buf   = new TerminalBuffer(120, 40);
        buf.Write(bytes);
        AssertInvariants(buf);
        Assert.False(buf.IsAltScreen);
        // Screen should be non-empty.
        Assert.NotEqual(string.Empty, buf.ScreenText().Replace("\n", "").Trim());
    }

    // ------------------------------------------------------------------
    // 4. git log (SGR 33 yellow hashes)
    // ------------------------------------------------------------------

    [Fact]
    public void Replay_GitLog_CarriesColorAndCommitHashes()
    {
        var bytes = Load("git-log.bin");
        var buf   = new TerminalBuffer(120, 40);
        buf.Write(bytes);
        AssertInvariants(buf);

        // git log --oneline --graph produces: "* <yellow>hash</yellow> subject"
        // We verify a yellow cell somewhere on row 0 where the hash sits.
        var row0 = buf.GetVisibleRow(0);
        bool foundYellow = false;
        for (int c = 0; c < buf.Cols; c++)
        {
            if (row0[c].Rune != 0 && (row0[c].Flags & CellFlags.FgRgb) == 0
                && row0[c].FgIndex == 3)
            {
                foundYellow = true; break;
            }
        }
        Assert.True(foundYellow, "expected a yellow (idx 3) cell in git-log row 0");
    }

    // ------------------------------------------------------------------
    // 5. vim enter + exit — alt-screen must be restored at the end
    // ------------------------------------------------------------------

    [Fact]
    public void Replay_VimEnterExit_ReturnsToPrimary()
    {
        var bytes = Load("vim.bin");
        var buf   = new TerminalBuffer(120, 40);
        buf.Write(bytes);
        AssertInvariants(buf);

        // Capture ends with CSI ?1049l — primary screen is active again.
        Assert.False(buf.IsAltScreen);
        Assert.True(buf.CursorVisible);
    }

    // ------------------------------------------------------------------
    // 6. less enter + exit — same alt-screen invariant
    // ------------------------------------------------------------------

    [Fact]
    public void Replay_LessEnterExit_ReturnsToPrimary()
    {
        var bytes = Load("less.bin");
        var buf   = new TerminalBuffer(120, 40);
        buf.Write(bytes);
        AssertInvariants(buf);
        Assert.False(buf.IsAltScreen);
    }

    // ------------------------------------------------------------------
    // 7. SGR rainbow — indexed foreground colours 30..37 in sequence
    // ------------------------------------------------------------------

    [Fact]
    public void Replay_SgrRainbow_HasDistinctFgIndicesInRow()
    {
        var bytes = Load("sgr-rainbow.bin");
        var buf   = new TerminalBuffer(120, 40);
        buf.Write(bytes);
        AssertInvariants(buf);

        // Row 0 should contain cells with at least 4 distinct non-default
        // FgIndex values (one per colour we printed). We just look for
        // any fg-index-carrying cell and count uniques.
        var row0 = buf.GetVisibleRow(0);
        var seen = new System.Collections.Generic.HashSet<byte>();
        for (int c = 0; c < buf.Cols; c++)
        {
            if (row0[c].Rune != 0 && (row0[c].Flags & CellFlags.FgRgb) == 0
                && row0[c].FgIndex != 0)
                seen.Add(row0[c].FgIndex);
        }
        Assert.True(seen.Count >= 4, $"expected >=4 distinct fg indices, got {seen.Count}");
    }

    // ------------------------------------------------------------------
    // 8. top -l 1: lots of CSI motion, SGR and CUP — just survival
    // ------------------------------------------------------------------

    [Fact]
    public void Replay_Top_ParsesWithoutThrow()
    {
        var bytes = Load("top.bin");
        var buf   = new TerminalBuffer(120, 40);
        buf.Write(bytes);
        AssertInvariants(buf);
        // top -l 1 doesn't enter alt-screen in non-interactive mode,
        // but we only assert survival here — every other shape of
        // output goes through the same code path as its interactive
        // cousin, and the bytes are plenty adversarial.
    }

    // ------------------------------------------------------------------
    // 9. Chunked feed — same capture, fed byte-by-byte, must produce
    //    the same final state as a single-shot Write (parser state
    //    machine splits cleanly across buffers).
    // ------------------------------------------------------------------

    [Fact]
    public void Replay_Truecolor_ChunkedFeedMatchesOneShot()
    {
        var bytes = Load("truecolor.bin");
        var oneShot = new TerminalBuffer(80, 24);
        oneShot.Write(bytes);

        var chunked = new TerminalBuffer(80, 24);
        for (int i = 0; i < bytes.Length; i++) chunked.Write(bytes.AsSpan(i, 1));

        AssertInvariants(oneShot);
        AssertInvariants(chunked);
        Assert.Equal(oneShot.ScreenText(), chunked.ScreenText());
        Assert.Equal(oneShot.CursorRow, chunked.CursorRow);
        Assert.Equal(oneShot.CursorCol, chunked.CursorCol);
    }

    // ------------------------------------------------------------------
    // 10. Chunked feed of the larger vim.bin — alt-screen state must
    //     end the same way in both feed modes.
    // ------------------------------------------------------------------

    [Fact]
    public void Replay_Vim_ChunkedFeedEndsOnPrimaryScreen()
    {
        var bytes = Load("vim.bin");
        var chunked = new TerminalBuffer(120, 40);
        // Feed in 17-byte chunks to stress the parser's split handling.
        const int step = 17;
        for (int i = 0; i < bytes.Length; i += step)
            chunked.Write(bytes.AsSpan(i, Math.Min(step, bytes.Length - i)));

        AssertInvariants(chunked);
        Assert.False(chunked.IsAltScreen);
    }
}
