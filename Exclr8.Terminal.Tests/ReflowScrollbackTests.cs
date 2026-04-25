using Xunit;
using static Exclr8.Terminal.Tests.TestHelpers;

namespace Exclr8.Terminal.Tests;

/// <summary>
/// Reflow needs to rewrap scrollback content, not just live screen.
/// These tests pin the behaviour: write content that auto-wraps, push
/// it into scrollback, then resize and walk the scrollback with the
/// new column count.
/// </summary>
public class ReflowScrollbackTests
{
    private static string ScrollbackRowText(Exclr8.Terminal.Buffer.TerminalBuffer buf, int absRow)
    {
        var row = buf.GetRowForRender(absRow - (buf.ScrollbackCount - buf.ScrollOffset));
        // Easier: read directly via the scrollback ring through Buffer.
        // Use viewport scrolled to top so absolute rows map cleanly.
        buf.SetScrollOffset(buf.ScrollbackCount);
        var cells = buf.GetRowForRender(absRow);
        if (cells == null) return "<null>";
        var sb = new System.Text.StringBuilder();
        foreach (var c in cells)
        {
            if ((c.Flags2 & Exclr8.Terminal.Buffer.CellFlags2.IsContinuation) != 0) continue;
            sb.Append(c.Rune == 0 ? ' ' : char.ConvertFromUtf32(c.Rune));
        }
        return sb.ToString().TrimEnd(' ');
    }

    [Fact]
    public void Resize_Narrowing_RewrapsScrollbackContent()
    {
        var buf = NewBuffer(20, 3);
        buf.ScrollbackLimit = 100;
        // Write a line that fits exactly: 20 chars, no wrap. Then push
        // it into scrollback by emitting more lines.
        buf.Feed("0123456789abcdefghij");
        buf.Feed("\r\nlineB\r\nlineC\r\nlineD\r\nlineE");
        // Now "0123...j" is in scrollback. Narrow to 10 cols. The
        // line was 20 chars long with no wrap flag — should reflow
        // into TWO 10-cell rows.
        buf.Resize(10, 3);
        // Walk scrollback for a row whose content starts with "0".
        bool found0to9 = false, foundAtoJ = false;
        for (int i = 0; i < buf.ScrollbackCount; i++)
        {
            var t = ScrollbackRowText(buf, i);
            if (t == "0123456789") found0to9 = true;
            if (t == "abcdefghij") foundAtoJ = true;
        }
        Assert.True(found0to9, "first half of long line should be in scrollback");
        Assert.True(foundAtoJ, "second half of long line should be in scrollback");
    }

    [Fact]
    public void Resize_Narrowing_TenLongLines_NothingTruncated()
    {
        var buf = NewBuffer(40, 4);
        buf.ScrollbackLimit = 100;
        // 10 lines of 35 'A's. Each fits in 40 cols (no wrap), then \r\n.
        for (int i = 0; i < 10; i++)
            buf.Feed(new string('A', 35) + "\r\n");
        // Resize to 20 cols. Each 35-char logical line should split into
        // a 20-char row + a 15-char wrapped row — total 70 rows worth of
        // content distributed between scrollback and live screen.
        buf.Resize(20, 4);
        // Walk all rows in absolute order.
        int totalAs = 0;
        for (int abs = 0; abs < buf.ScrollbackCount + buf.Rows; abs++)
        {
            var t = ScrollbackRowText(buf, abs);
            totalAs += t.Length; // trimmed text — only meaningful chars
        }
        // 10 lines * 35 chars = 350 'A's expected after reflow.
        Assert.Equal(350, totalAs);
    }

    [Fact]
    public void Resize_NarrowThenWide_RejoinsAcrossScrollbackBoundary()
    {
        // The user-reported scenario: print one long auto-wrapped line,
        // resize narrow (which splits it across many narrow rows that
        // span scrollback + live screen), then resize wide. Reflow on
        // widen must rejoin the WHOLE line — including the boundary
        // between the last scrollback row and the first live-screen
        // row. Earlier iteration broke this: the last scrollback row
        // closed its logical line prematurely, leaving the first few
        // narrow rows un-rejoined.
        var buf = NewBuffer(200, 30);
        buf.ScrollbackLimit = 5000;
        var s = new System.Text.StringBuilder();
        for (int i = 1; i <= 550; i++) s.Append(i).Append(' ');
        buf.Feed(s.ToString());
        buf.Feed("\r\n");

        buf.Resize(30, 10);  // narrow + short — content spills into scrollback
        buf.Resize(200, 30); // back to wide

        // Walk all rows top→bottom, concatenate trimmed text. The
        // numbers 1..550 must appear in order with NO physical-row
        // gaps — which would be the symptom of a failed rejoin (the
        // narrow rows would have left mid-number boundaries).
        var assembled = new System.Text.StringBuilder();
        int totalRows = buf.ScrollbackCount + buf.Rows;
        buf.SetScrollOffset(buf.ScrollbackCount);
        for (int abs = 0; abs < totalRows; abs++)
        {
            var cells = buf.GetRowForRender(abs);
            if (cells == null) continue;
            // Trim trailing spaces on each row, then concat with a single
            // space separator. If reflow rejoined correctly the result
            // is a clean "1 2 3 ... 550" sequence.
            var rowText = new System.Text.StringBuilder();
            foreach (var c in cells)
            {
                if ((c.Flags2 & Exclr8.Terminal.Buffer.CellFlags2.IsContinuation) != 0) continue;
                rowText.Append(c.Rune == 0 ? ' ' : char.ConvertFromUtf32(c.Rune));
            }
            assembled.Append(rowText.ToString().TrimEnd(' '));
            assembled.Append(' ');
        }
        var content = assembled.ToString();
        // Spot-check numbers across the boundary that used to break:
        // 86 was the last on row 3 of the narrow layout; 87 started
        // row 4. After reflow they need to appear in sequence.
        Assert.Contains("84 85 86 87 88 89", content);
        // And right around the start of the line.
        Assert.Contains("1 2 3 4 5 6", content);
        // And near the end.
        Assert.Contains("547 548 549 550", content);
    }

    [Fact]
    public void Resize_Widening_RejoinsWrappedScrollbackContent()
    {
        var buf = NewBuffer(10, 3);
        buf.ScrollbackLimit = 100;
        // Write 15 chars at 10 cols → row 0 = "0123456789", row 1 =
        // "abcde     " with wrap flag set. Then push to scrollback.
        buf.Feed("0123456789abcde");
        buf.Feed("\r\nlineB\r\nlineC\r\nlineD");
        buf.Resize(20, 3);
        // After widening, "0123456789abcde" should be one logical row
        // in scrollback.
        bool foundJoined = false;
        for (int i = 0; i < buf.ScrollbackCount; i++)
        {
            var t = ScrollbackRowText(buf, i);
            if (t == "0123456789abcde") foundJoined = true;
        }
        Assert.True(foundJoined, "wrapped scrollback line should rejoin on widen");
    }
}
