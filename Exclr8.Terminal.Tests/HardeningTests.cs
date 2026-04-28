using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Exclr8.Terminal;
using Exclr8.Terminal.Buffer;
using Xunit;
using static Exclr8.Terminal.Tests.TestHelpers;

namespace Exclr8.Terminal.Tests;

/// <summary>
/// Coverage for the production-hardening pass: protocol tracing,
/// link-provider guardrails. (Write coalescing and link policy are
/// control-level paths that need an Avalonia dispatcher, so they're
/// covered by integration smoke runs rather than unit tests.)
///
/// <para><b>Why these tests use unique markers, not message counts:</b>
/// <see cref="TerminalLog.EnableProtocolTrace"/> and
/// <see cref="TerminalLog.Trace"/> are process-global statics. xUnit
/// runs test classes in parallel by default, so a fuzz test in
/// another class can fire a trace concurrent with our assertions.
/// We assert on <c>messages.Any(m =&gt; m.Contains("specific
/// marker"))</c> so unrelated parallel traces are ignored.</para>
/// </summary>
public class HardeningTests
{
    /// <summary>Mutex for tests that flip TerminalLog statics. Held
    /// for the entire body of each such test so the assignment to
    /// <c>Trace</c> + the assertion + the restore in finally happens
    /// atomically with respect to other trace-mutating tests in this
    /// class.</summary>
    private static readonly object TraceLock = new();

    [Fact]
    public void Trace_FiresForUnhandledCsiWhenEnabled()
    {
        lock (TraceLock)
        {
            // ConcurrentBag because parallel test classes whose
            // dispatches go through TerminalLog.Trace while our flag
            // is on can write here from any thread.
            var messages = new ConcurrentBag<string>();
            var prevTrace = TerminalLog.Trace;
            TerminalLog.Trace = m => messages.Add(m);
            TerminalLog.EnableProtocolTrace = true;
            try
            {
                var buf = NewBuffer();
                buf.Feed(CSI + " z"); // intermediates=" " final='z' — unhandled
                Assert.Contains(messages,
                    m => m.Contains("unhandled CSI") && m.Contains("final='z'"));
            }
            finally
            {
                TerminalLog.Trace = prevTrace;
                TerminalLog.EnableProtocolTrace = false;
            }
        }
    }

    [Fact]
    public void Trace_FiresForUnknownDecMode()
    {
        lock (TraceLock)
        {
            // ConcurrentBag because parallel test classes whose
            // dispatches go through TerminalLog.Trace while our flag
            // is on can write here from any thread.
            var messages = new ConcurrentBag<string>();
            var prevTrace = TerminalLog.Trace;
            TerminalLog.Trace = m => messages.Add(m);
            TerminalLog.EnableProtocolTrace = true;
            try
            {
                var buf = NewBuffer();
                buf.Feed(CSI + "?9999h"); // bogus DEC mode
                Assert.Contains(messages, m => m.Contains("DECSET 9999"));
            }
            finally
            {
                TerminalLog.Trace = prevTrace;
                TerminalLog.EnableProtocolTrace = false;
            }
        }
    }

    [Fact]
    public void Trace_FiresForUnknownOscId()
    {
        lock (TraceLock)
        {
            // ConcurrentBag because parallel test classes whose
            // dispatches go through TerminalLog.Trace while our flag
            // is on can write here from any thread.
            var messages = new ConcurrentBag<string>();
            var prevTrace = TerminalLog.Trace;
            TerminalLog.Trace = m => messages.Add(m);
            TerminalLog.EnableProtocolTrace = true;
            try
            {
                var buf = NewBuffer();
                buf.Feed(OSC + "9999;hello" + ST);
                Assert.Contains(messages, m => m.Contains("unhandled OSC: 9999"));
            }
            finally
            {
                TerminalLog.Trace = prevTrace;
                TerminalLog.EnableProtocolTrace = false;
            }
        }
    }

    [Fact]
    public void Trace_RegisteredOscHandler_PreventsTrace()
    {
        // An OSC id with a registered custom handler is "handled" —
        // no fall-through, no trace. Sanity-checks that the trace
        // doesn't false-positive on hosted extensions.
        lock (TraceLock)
        {
            // ConcurrentBag because parallel test classes whose
            // dispatches go through TerminalLog.Trace while our flag
            // is on can write here from any thread.
            var messages = new ConcurrentBag<string>();
            var prevTrace = TerminalLog.Trace;
            TerminalLog.Trace = m => messages.Add(m);
            TerminalLog.EnableProtocolTrace = true;
            try
            {
                var buf = NewBuffer();
                using var _ = buf.RegisterOscHandler(7777, _ => true);
                buf.Feed(OSC + "7777;payload" + ST);
                Assert.DoesNotContain(messages, m => m.Contains("OSC: 7777"));
            }
            finally
            {
                TerminalLog.Trace = prevTrace;
                TerminalLog.EnableProtocolTrace = false;
            }
        }
    }

    [Fact]
    public void Osc8_StandardCloseClearsHyperlink()
    {
        var buf = NewBuffer(40, 4);
        buf.Feed(OSC + "8;;https://exclr8.ai" + ST);
        buf.Feed("link");
        buf.Feed(OSC + "8;;" + ST); // close
        buf.Feed("plain");
        var row = buf.GetVisibleRow(0);
        // First 4 cells linked.
        Assert.NotEqual(0, row[0].HyperlinkId);
        Assert.NotEqual(0, row[3].HyperlinkId);
        // After close, the next 5 cells are NOT linked.
        Assert.Equal(0, row[4].HyperlinkId);
        Assert.Equal(0, row[8].HyperlinkId);
    }

    [Fact]
    public void Osc8_ShortCloseFormatAlsoClears()
    {
        // Some implementations emit "OSC 8 ; ST" (one semicolon, no
        // URL slot) as a close. Defensive parsing should accept it.
        var buf = NewBuffer(40, 4);
        buf.Feed(OSC + "8;;https://exclr8.ai" + ST);
        buf.Feed("link");
        buf.Feed(OSC + "8;" + ST); // shorter close form
        buf.Feed("plain");
        var row = buf.GetVisibleRow(0);
        Assert.NotEqual(0, row[0].HyperlinkId);
        Assert.Equal(0, row[4].HyperlinkId);
    }

    [Fact]
    public void ClearActiveHyperlink_RecoversFromStuckOsc8()
    {
        // Simulate the upstream-dropped-close scenario: the open
        // arrives, but the close never does. Without a recovery
        // path, every subsequent cell is linked. The host calls
        // ClearActiveHyperlink and the bleed stops.
        var buf = NewBuffer(40, 4);
        buf.Feed(OSC + "8;;https://exclr8.ai" + ST);
        buf.Feed("stuck");
        // No close arrives. Anything we type now is linked.
        buf.Feed(" more");
        var row = buf.GetVisibleRow(0);
        Assert.NotEqual(0, row[6].HyperlinkId); // 'm' of " more"

        // Host triggers recovery.
        buf.ClearActiveHyperlink();
        buf.Feed(" clean");
        Assert.Equal(0, row[12].HyperlinkId); // 'c' of " clean" — first cell after recovery
    }

    [Fact]
    public void SoftReset_ClearsSgrPenButPreservesScreen()
    {
        var buf = NewBuffer(20, 4);
        buf.Feed(CSI + "31m"); // red foreground
        buf.Feed("RED");
        buf.SoftResetTerminal();
        buf.Feed("plain");
        var row = buf.GetVisibleRow(0);
        // Screen contents preserved.
        Assert.Equal('R', row[0].Rune);
        Assert.Equal('p', row[3].Rune);
        // Pen reset: subsequent cells not red.
        Assert.Equal(0, row[3].FgIndex);
    }

    [Fact]
    public void Reset_ClearsScreenAndScrollback()
    {
        var buf = NewBuffer(20, 4);
        buf.ScrollbackLimit = 100;
        for (int i = 0; i < 6; i++) buf.Feed($"L{i}\r\n");
        Assert.True(buf.ScrollbackCount > 0);
        buf.ResetTerminal();
        Assert.Equal(0, buf.ScrollbackCount);
        // Screen blank.
        Assert.Equal('\0', (char)buf.GetVisibleRow(0)[0].Rune);
    }

    [Fact]
    public void Osc0_RepeatedSameTitle_DoesNotRefireEvent()
    {
        // Bash/zsh prompt frameworks emit OSC 0 on every prompt; the
        // string is usually identical across redraws. Dedupe so
        // subscribers don't get a per-prompt event chain that just
        // compare-equals back out.
        var buf = NewBuffer();
        int titleCount = 0, iconCount = 0;
        buf.TitleChanged    += (_, _) => titleCount++;
        buf.IconNameChanged += (_, _) => iconCount++;
        buf.Feed(OSC + "0;mytitle" + ST);
        buf.Feed(OSC + "0;mytitle" + ST);
        buf.Feed(OSC + "0;mytitle" + ST);
        Assert.Equal(1, titleCount);
        Assert.Equal(1, iconCount);
        // Different title fires once.
        buf.Feed(OSC + "0;newtitle" + ST);
        Assert.Equal(2, titleCount);
        Assert.Equal(2, iconCount);
    }

    [Fact]
    public void RowText_MightContainUrl_FastReject()
    {
        // Plain text rows return false → renderer skips the
        // text-materialise + regex run.
        var buf = NewBuffer(40, 4);
        buf.Feed("just plain text here, no scheme");
        var cells = buf.GetVisibleRow(0);
        Assert.False(RowText.MightContainUrl(cells));

        // Anything containing :/ returns true.
        var buf2 = NewBuffer(40, 4);
        buf2.Feed("see https://example.com");
        Assert.True(RowText.MightContainUrl(buf2.GetVisibleRow(0)));
    }

    [Fact]
    public void RowText_BuildInto_AcceptsCallerOwnedBuffers()
    {
        var buf = NewBuffer(20, 4);
        buf.Feed("abc");
        var cells = buf.GetVisibleRow(0);
        var sb = new System.Text.StringBuilder();
        var map = new int[cells.Length * 2];
        int len = RowText.BuildInto(cells, sb, map);
        Assert.Equal(cells.Length, len); // padded with spaces
        Assert.Equal('a', sb[0]);
        Assert.Equal('b', sb[1]);
        Assert.Equal(0, map[0]);
        Assert.Equal(1, map[1]);
    }

    [Fact]
    public void LinkProviderCap_FloodingProviderDoesNotHangHitTest()
    {
        // A provider that returns thousands of matches per row
        // shouldn't tank the click-hit path. We can't directly
        // observe the renderer's per-row cap from a unit test, but
        // we can confirm the provider's contract works with the
        // cap-respecting consumer pattern.
        var buf = NewBuffer(20, 4);
        buf.Feed("hit me");
        var rowText = RowText.Build(buf.GetVisibleRow(0), out _);
        var p = new FloodProvider();
        var capped = p.Provide(rowText).Take(64).ToList();
        Assert.Equal(64, capped.Count);
    }

    private sealed class FloodProvider : ILinkProvider
    {
        public IEnumerable<TerminalLink> Provide(string rowText)
        {
            for (int i = 0; i < 1000; i++)
                yield return new TerminalLink(0, 1, $"https://example/{i}");
        }
    }

    // ---- Wide-cell pair integrity across mutation operations ----
    //
    // Streaming output from CLIs that drive incremental redraws (TUI
    // status lines, progress bars, AI-assistant token-stream UI) can
    // mix cursor-positioning + cell-erase / cell-shift sequences with
    // wide characters. If a mutation lands on one half of a wide pair,
    // the *other* half can be left orphaned: a stale IsWide with no
    // following IsContinuation, or a stale IsContinuation with no
    // preceding IsWide. The renderer skips IsContinuation cells, so
    // orphans render as invisible gaps — exactly the "double-space
    // and missing characters" symptom seen in long Claude Code
    // streaming sessions. These tests pin the invariant: every cell
    // mutation path repairs wide-pair integrity at its boundaries.

    [Fact]
    public void EraseChars_OnWideLeftHalf_ClearsOrphanContinuation()
    {
        var buf = NewBuffer(10, 4);
        buf.Feed("A中B");                       // A=col0, 中=col1+2, B=col3
        var row = buf.GetVisibleRow(0);
        Assert.True((row[1].Flags2 & CellFlags2.IsWide) != 0);
        Assert.True((row[2].Flags2 & CellFlags2.IsContinuation) != 0);

        buf.Feed(CSI + "1;2H");                 // cursor → col 1
        buf.Feed(CSI + "1X");                   // ECH 1 — erase the wide-left
        Assert.False((row[1].Flags2 & CellFlags2.IsWide)         != 0);
        Assert.False((row[2].Flags2 & CellFlags2.IsContinuation) != 0,
            "EraseChars on wide-left must scrub the orphan IsContinuation flag.");
    }

    [Fact]
    public void EraseChars_OnWideRightHalf_ClearsOrphanWide()
    {
        var buf = NewBuffer(10, 4);
        buf.Feed("A中B");
        var row = buf.GetVisibleRow(0);

        buf.Feed(CSI + "1;3H");                 // cursor → col 2 (the continuation)
        buf.Feed(CSI + "1X");                   // ECH 1 — erase the continuation
        Assert.False((row[1].Flags2 & CellFlags2.IsWide)         != 0,
            "EraseChars on wide-right must scrub the orphan IsWide flag.");
        Assert.False((row[2].Flags2 & CellFlags2.IsContinuation) != 0);
    }

    [Fact]
    public void DeleteChars_AcrossWidePair_DoesNotLeaveOrphan()
    {
        var buf = NewBuffer(10, 4);
        buf.Feed("A中BCD");                     // A 中(2) B C D
        buf.Feed(CSI + "1;2H");                 // cursor → col 1
        buf.Feed(CSI + "1P");                   // DCH 1 — delete the wide-left
        var row = buf.GetVisibleRow(0);
        // After DCH 1 with cursor at col 1, content shifts left. No
        // cell should retain orphan wide-pair flags.
        for (int c = 0; c < buf.Cols; c++)
        {
            bool isCont = (row[c].Flags2 & CellFlags2.IsContinuation) != 0;
            bool isWide = (row[c].Flags2 & CellFlags2.IsWide)         != 0;
            if (isCont)
            {
                Assert.True(c > 0
                    && (row[c - 1].Flags2 & CellFlags2.IsWide) != 0,
                    $"col {c}: orphan IsContinuation");
            }
            if (isWide)
            {
                Assert.True(c + 1 < buf.Cols
                    && (row[c + 1].Flags2 & CellFlags2.IsContinuation) != 0,
                    $"col {c}: orphan IsWide");
            }
        }
    }

    [Fact]
    public void InsertBlanks_PushingWidePair_DoesNotLeaveOrphan()
    {
        var buf = NewBuffer(10, 4);
        buf.Feed("A中BCD");
        buf.Feed(CSI + "1;2H");                 // cursor → col 1
        buf.Feed(CSI + "1@");                   // ICH 1 — insert one blank
        var row = buf.GetVisibleRow(0);
        for (int c = 0; c < buf.Cols; c++)
        {
            bool isCont = (row[c].Flags2 & CellFlags2.IsContinuation) != 0;
            bool isWide = (row[c].Flags2 & CellFlags2.IsWide)         != 0;
            if (isCont)
                Assert.True(c > 0
                    && (row[c - 1].Flags2 & CellFlags2.IsWide) != 0,
                    $"col {c}: orphan IsContinuation");
            if (isWide)
                Assert.True(c + 1 < buf.Cols
                    && (row[c + 1].Flags2 & CellFlags2.IsContinuation) != 0,
                    $"col {c}: orphan IsWide");
        }
    }

    [Fact]
    public void ClearScreenAndScrollback_NoOscMarker_PreservesCursorRow()
    {
        // No OSC 133 prompt-tracking → preserve just the cursor row
        // (typically the prompt + in-progress input). Everything
        // above wiped, scrollback dropped.
        var buf = NewBuffer(20, 5);
        buf.ScrollbackLimit = 100;
        for (int i = 0; i < 8; i++) buf.Feed($"L{i}\r\n");
        // Cursor is now on a blank row after "L7\r\n". Type a
        // pseudo-prompt onto the cursor row.
        buf.Feed("$ ");
        Assert.True(buf.ScrollbackCount > 0);

        buf.ClearScreenAndScrollback();

        Assert.Equal(0, buf.ScrollbackCount);
        // Cursor row content survived at row 0.
        Assert.Equal('$', (char)buf.GetVisibleRow(0)[0].Rune);
        Assert.Equal(' ', (char)buf.GetVisibleRow(0)[1].Rune);
        // Other rows blank.
        Assert.Equal(0, buf.GetVisibleRow(1)[0].Rune);
        // Cursor is at row 0 (its preserved row).
        Assert.Equal(0, buf.CursorRow);
    }

    [Fact]
    public void ClearScreenAndScrollback_WithOsc133_PreservesPromptBlock()
    {
        // OSC 133 prompt-tracking → preserve [PromptStart, cursor]
        // inclusive. Multi-line prompts and wrapped input survive.
        var buf = NewBuffer(40, 6);
        buf.ScrollbackLimit = 100;
        // Some output above the prompt.
        for (int i = 0; i < 4; i++) buf.Feed($"output{i}\r\n");
        // Two-line prompt + input on the cursor's row.
        buf.Feed(OSC + "133;A" + ST);  // prompt-start marker fires here
        buf.Feed("~/code on  main\r\n");
        buf.Feed("> in-progress");
        // Cursor is on row containing "> in-progress".
        int cursorRowBefore = buf.CursorRow;

        buf.ClearScreenAndScrollback();

        Assert.Equal(0, buf.ScrollbackCount);
        // Prompt's first line snapped to row 0.
        Assert.StartsWith("~/code on", buf.RowText(0).TrimEnd());
        // Input line snapped to row 1.
        Assert.StartsWith("> in-progress", buf.RowText(1).TrimEnd());
        // Cursor preserved relative to the block — was at row N
        // (input line); now at row 1.
        Assert.Equal(1, buf.CursorRow);
    }

    [Fact]
    public void ClearScreenAndScrollback_OnAltScreen_IsNoOp()
    {
        // Alt-screen TUIs own their painted state; Cmd+K shouldn't
        // wipe it from under them. iTerm2 and friends do wipe it
        // anyway and rely on TUI redraw via SIGWINCH; we choose the
        // safer no-op since we have no such redraw protocol on hand.
        var buf = NewBuffer(20, 5);
        buf.Feed(CSI + "?1049h");           // enter alt-screen
        buf.Feed("ALT-CONTENT");
        var snapshot = buf.RowText(0);

        buf.ClearScreenAndScrollback();

        Assert.Equal(snapshot, buf.RowText(0));
    }

    [Fact]
    public void ClearScreenAndScrollback_PreservesSgrPen()
    {
        // Distinct from full Reset: pen + DEC modes survive so a
        // mid-session "clear my screen" doesn't kill the user's
        // colour scheme or app modes.
        var buf = NewBuffer(20, 4);
        buf.Feed(CSI + "31m");        // red foreground pen
        buf.Feed(CSI + "?25l");       // cursor invisible
        buf.Feed("text");
        buf.ClearScreenAndScrollback();
        // Pen state should still report red on the next print.
        buf.Feed("X");
        // The "text" was preserved on row 0; "X" appended after it.
        Assert.Equal(1, (int)buf.GetVisibleRow(0)[4].FgIndex); // 'X' at col 4 — SGR 31 = idx 1
        // DECTCEM still off.
        Assert.False(buf.CursorVisible);
    }

    [Fact]
    public void TerminalCell_PackedTo24Bytes()
    {
        // Pinning the field-order packing — adding a new field
        // without slotting it correctly will trip this test and force
        // a deliberate re-think rather than silently bloating the
        // scrollback footprint. With 5000 lines × 80 cols, every
        // extra byte is +400 KB of working set.
        Assert.Equal(24, System.Runtime.CompilerServices.Unsafe.SizeOf<TerminalCell>());
    }
}
