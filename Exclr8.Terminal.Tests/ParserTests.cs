using System;
using System.Collections.Generic;
using System.Text;
using Exclr8.Terminal.Parser;
using Xunit;

namespace Exclr8.Terminal.Tests;

/// <summary>
/// Structural tests for <see cref="VtParser"/>. Uses a recording
/// <see cref="IParserActions"/> so we can assert exact dispatch counts
/// + payloads without going through the buffer. Mirrors the test
/// matrix in xterm.js's <c>EscapeSequenceParser.test.ts</c>.
/// </summary>
public class ParserTests
{
    private sealed class Recorder : IParserActions
    {
        public StringBuilder Printed = new();
        public List<byte> Executes = new();
        public List<(char Final, int[] Params, string Intermediates, char Prefix)> Csi = new();
        public List<(char Final, string Intermediates)> Esc = new();
        public List<string> Osc = new();
        public List<byte> Replies = new();

        public void Print(int r) => Printed.Append(char.ConvertFromUtf32(r));
        public void Execute(byte c) => Executes.Add(c);
        public void CsiDispatch(char f, int[] p, string i, char pr) => Csi.Add((f, p, i, pr));
        public void EscDispatch(char f, string i) => Esc.Add((f, i));
        public void OscDispatch(string s) => Osc.Add(s);
        public void ReplyToPty(ReadOnlySpan<byte> b) { foreach (var x in b) Replies.Add(x); }
    }

    private static Recorder ParseBytes(params byte[] data)
    {
        var rec = new Recorder();
        new VtParser(rec).Parse(data);
        return rec;
    }

    private static Recorder ParseString(string s) => ParseBytes(Encoding.UTF8.GetBytes(s));

    // ------------------------------------------------------------------
    // Ground-state printing
    // ------------------------------------------------------------------

    [Fact]
    public void Print_PlainAsciiReportedAsPrintableRunes()
    {
        var r = ParseString("hello");
        Assert.Equal("hello", r.Printed.ToString());
        Assert.Empty(r.Csi);
        Assert.Empty(r.Esc);
    }

    [Fact]
    public void Print_UTF8MultibyteAssembledBeforeDispatch()
    {
        // é = 0xC3 0xA9, ✓ = 0xE2 0x9C 0x93, 😀 = 0xF0 0x9F 0x98 0x80
        var r = ParseString("é✓\U0001f600");
        Assert.Equal("é✓\U0001f600", r.Printed.ToString());
    }

    [Fact]
    public void Print_InvalidUTF8ContinuationResync()
    {
        // 0xC3 with a bad continuation byte — parser should drop the
        // partial sequence and pick up normally with 'A'.
        var r = ParseBytes(0xC3, 0x20, (byte)'A');
        // Space (0x20) is ASCII printable, 'A' is ASCII printable.
        Assert.Contains("A", r.Printed.ToString());
    }

    [Fact]
    public void Execute_C0BytesDispatchedSeparately()
    {
        var r = ParseBytes((byte)'A', 0x08, (byte)'B', 0x0A);
        Assert.Equal("AB", r.Printed.ToString());
        Assert.Equal(new byte[] { 0x08, 0x0A }, r.Executes);
    }

    [Fact]
    public void DEL_InGroundIsExecuted()
    {
        var r = ParseBytes(0x7F);
        Assert.Single(r.Executes);
        Assert.Equal(0x7F, r.Executes[0]);
    }

    // ------------------------------------------------------------------
    // CSI dispatch
    // ------------------------------------------------------------------

    [Fact]
    public void Csi_SimpleCupNoParams()
    {
        var r = ParseString("\x1b[H");
        var c = Assert.Single(r.Csi);
        Assert.Equal('H', c.Final);
        Assert.Equal(new[] { 0 }, c.Params);
        Assert.Equal("", c.Intermediates);
        Assert.Equal((char)0, c.Prefix);
    }

    [Fact]
    public void Csi_SingleParam()
    {
        var r = ParseString("\x1b[5A");
        var c = Assert.Single(r.Csi);
        Assert.Equal('A', c.Final);
        Assert.Equal(new[] { 5 }, c.Params);
    }

    [Fact]
    public void Csi_MultipleParamsSemiSeparated()
    {
        var r = ParseString("\x1b[12;34H");
        var c = Assert.Single(r.Csi);
        Assert.Equal(new[] { 12, 34 }, c.Params);
    }

    [Fact]
    public void Csi_EmptyParamAndTrailingSemi()
    {
        // `ESC [ ; 5 H` — first param defaults to 0, second is 5.
        var r = ParseString("\x1b[;5H");
        var c = Assert.Single(r.Csi);
        Assert.Equal(new[] { 0, 5 }, c.Params);
    }

    [Fact]
    public void Csi_PrivatePrefixCaptured()
    {
        var r = ParseString("\x1b[?25h");
        var c = Assert.Single(r.Csi);
        Assert.Equal('?', c.Prefix);
        Assert.Equal('h', c.Final);
        Assert.Equal(new[] { 25 }, c.Params);
    }

    [Fact]
    public void Csi_IntermediateBytesCaptured()
    {
        // DECSCUSR: ESC [ 2 SP q
        var r = ParseString("\x1b[2 q");
        var c = Assert.Single(r.Csi);
        Assert.Equal('q', c.Final);
        Assert.Equal(" ", c.Intermediates);
        Assert.Equal(new[] { 2 }, c.Params);
    }

    [Fact]
    public void Csi_ColonSubparamTreatedAsSemi()
    {
        // Colon sub-params have two meanings in SGR and the parser
        // must distinguish:
        //
        //  (a) `\e[38:2::255:128:0m` — truecolor via colons. These
        //      components MUST reach the ApplyExtColor dispatcher, so
        //      after a primary SGR 38 or 48 we treat ':' like ';'.
        //  (b) `\e[4:3m` — curly underline. Sub-param 3 modifies the
        //      underline style; it must NOT turn into a separate SGR
        //      primary param (that would cause SGR 4 + SGR 3 = adds
        //      italic, the classic "line I'm typing got italicised"
        //      bug). Sub-params here are swallowed.

        // Truecolor form still surfaces all components as primary
        // params so the SGR handler works the same as the ';' form.
        var r1 = ParseString("\x1b[38:2::255:128:0m");
        var c1 = Assert.Single(r1.Csi);
        Assert.Equal('m', c1.Final);
        Assert.Contains(38, c1.Params);
        Assert.Contains(255, c1.Params);

        // Underline-style form collapses to just the primary — the
        // sub-param 3 does not become a stray SGR 3 italic op.
        var r2 = ParseString("\x1b[4:3m");
        var c2 = Assert.Single(r2.Csi);
        Assert.Equal('m', c2.Final);
        Assert.Equal(new[] { 4 }, c2.Params);
    }

    [Fact]
    public void Csi_LongParameterListClampedAt32()
    {
        // Generate 40 params — the parser stores up to 32, surplus dropped.
        var sb = new StringBuilder("\x1b[");
        for (int i = 0; i < 40; i++) { if (i > 0) sb.Append(';'); sb.Append(i + 1); }
        sb.Append('m');
        var r = ParseString(sb.ToString());
        var c = Assert.Single(r.Csi);
        Assert.True(c.Params.Length <= 32);
    }

    [Fact]
    public void Csi_CanBeInterruptedByCan()
    {
        // CAN (0x18) aborts parsing and returns to ground.
        var r = ParseBytes((byte)0x1B, (byte)'[', (byte)'5', 0x18, (byte)'A');
        // 'A' should be printed (ground again after CAN), no CSI dispatched.
        Assert.Equal("A", r.Printed.ToString());
        Assert.Empty(r.Csi);
    }

    [Fact]
    public void Csi_PrivateIntermediateMidParamIgnoresSequence()
    {
        // After params, putting < or ? should push to CsiIgnore and swallow.
        var r = ParseString("\x1b[5<zz");
        // z (0x7A) is a valid final - it triggers transition back to Ground
        // but from CsiIgnore state we drop the dispatch.
        Assert.Empty(r.Csi);
    }

    [Fact]
    public void Csi_MalformedNoFinalJustGround()
    {
        // ESC [ 5 ; 7 — incomplete, no final char
        var r = ParseString("\x1b[5;7");
        Assert.Empty(r.Csi);
    }

    // ------------------------------------------------------------------
    // ESC dispatch
    // ------------------------------------------------------------------

    [Fact]
    public void Esc_PlainEscWithFinal()
    {
        // ESC 7 = DECSC. Using bytes because `"\x1b7"` in a C# literal
        // would be greedily parsed as U+01B7 (C# eats up to four hex
        // digits after \x).
        var r = ParseBytes(0x1B, (byte)'7');
        var e = Assert.Single(r.Esc);
        Assert.Equal('7', e.Final);
        Assert.Equal("", e.Intermediates);
    }

    [Fact]
    public void Esc_WithIntermediate_SelectG0Charset()
    {
        // ESC ( 0 = select DEC special graphics for G0
        var r = ParseString("\x1b(0");
        var e = Assert.Single(r.Esc);
        Assert.Equal('0', e.Final);
        Assert.Equal("(", e.Intermediates);
    }

    [Fact]
    public void Esc_WithTwoIntermediates()
    {
        // ESC sp F — intermediates ['SP', ...] allowed, final 'F'
        var r = ParseString("\x1b #3");
        var e = Assert.Single(r.Esc);
        Assert.Equal('3', e.Final);
        Assert.Equal(" #", e.Intermediates);
    }

    // ------------------------------------------------------------------
    // OSC dispatch
    // ------------------------------------------------------------------

    [Fact]
    public void Osc_TerminatedByBel()
    {
        var r = ParseString("\x1b]0;window title\x07");
        var p = Assert.Single(r.Osc);
        Assert.Equal("0;window title", p);
    }

    [Fact]
    public void Osc_TerminatedByStringTerminator()
    {
        // ESC ] payload ESC \
        var r = ParseString("\x1b]2;title\x1b\\");
        var p = Assert.Single(r.Osc);
        Assert.Equal("2;title", p);
    }

    [Fact]
    public void Osc_EmptyPayloadStillDispatches()
    {
        var r = ParseString("\x1b]\x07");
        var p = Assert.Single(r.Osc);
        Assert.Equal("", p);
    }

    [Fact]
    public void Osc_EmbeddedC0BytesIgnored()
    {
        // 0x01 embedded between 'a' and 'b', terminated by BEL. Written
        // byte-wise because `\x01b` in a C# literal would parse as U+001B.
        var r = ParseBytes(0x1B, (byte)']', (byte)'0', (byte)';',
                           (byte)'a', 0x01, (byte)'b', 0x07);
        var p = Assert.Single(r.Osc);
        Assert.Equal("0;ab", p);
    }

    // ------------------------------------------------------------------
    // DCS / SOS / PM / APC — consumed silently, no dispatch
    // ------------------------------------------------------------------

    [Fact]
    public void Dcs_ConsumedUntilST_NoDispatch()
    {
        var r = ParseString("\x1bP1;2qSIXEL-DATA\x1b\\ABC");
        // After ST, "ABC" should be printed in ground.
        Assert.Equal("ABC", r.Printed.ToString());
        Assert.Empty(r.Csi);
    }

    [Fact]
    public void Sos_PM_APC_ConsumedSilently()
    {
        // ESC X ... ST (SOS)
        var a = ParseString("\x1bXanything\x1b\\");
        Assert.Empty(a.Printed.ToString()); // nothing printed from inside SOS
        // ESC ^ ... ST (PM)
        var b = ParseString("\x1b^blah\x1b\\HI");
        Assert.Equal("HI", b.Printed.ToString());
    }

    // ------------------------------------------------------------------
    // Anywhere transitions
    // ------------------------------------------------------------------

    [Fact]
    public void Escape_MidCsiAbortsAndRestartsSequence()
    {
        // `ESC [ 5 ESC [ H` — the second ESC aborts the first CSI.
        var r = ParseString("\x1b[5\x1b[H");
        var c = Assert.Single(r.Csi);
        Assert.Equal('H', c.Final);
    }

    [Fact]
    public void SUB_InMiddleOfCsiAbortsAndGoesToGround()
    {
        var r = ParseBytes((byte)0x1B, (byte)'[', (byte)'5', 0x1A, (byte)'X');
        Assert.Equal("X", r.Printed.ToString());
        Assert.Empty(r.Csi);
    }

    // ------------------------------------------------------------------
    // Parser.Reset — clears UTF-8 state + param state
    // ------------------------------------------------------------------

    [Fact]
    public void Reset_AbortsInProgressSequences()
    {
        var rec = new Recorder();
        var p = new VtParser(rec);
        // Start a CSI but don't finish it.
        p.Parse(Encoding.ASCII.GetBytes("\x1b[5;7"));
        p.Reset();
        p.Parse(new byte[] { (byte)'A' });
        Assert.Equal("A", rec.Printed.ToString());
        Assert.Empty(rec.Csi);
    }
}
