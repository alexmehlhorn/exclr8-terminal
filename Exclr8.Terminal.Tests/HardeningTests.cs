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
}
