using System;
using System.Text;
using Exclr8.Terminal.Buffer;

namespace Exclr8.Terminal.Tests;

/// <summary>
/// Shared helpers for feeding byte streams into a <see cref="TerminalBuffer"/>
/// and asserting the resulting grid state. The tests drive the buffer
/// via its public <see cref="TerminalBuffer.Write"/> surface, the same
/// way TerminalControl does — so if it passes here, it'll behave the
/// same when wired to a PTY.
/// </summary>
internal static class TestHelpers
{
    /// <summary>Create a buffer and feed it a UTF-8 string.</summary>
    public static TerminalBuffer NewBuffer(int cols = 10, int rows = 4)
    {
        var buf = new TerminalBuffer(cols, rows);
        // Disable scrollback by default — most tests only care about
        // live screen state; enable explicitly when a test needs it.
        buf.ScrollbackLimit = 5000;
        return buf;
    }

    /// <summary>Write a UTF-8 string to the buffer.</summary>
    public static void Feed(this TerminalBuffer buf, string s)
        => buf.Write(Encoding.UTF8.GetBytes(s));

    /// <summary>Write raw bytes.</summary>
    public static void FeedBytes(this TerminalBuffer buf, params byte[] bytes)
        => buf.Write(bytes);

    /// <summary>
    /// Serialise the entire visible screen to newline-joined strings,
    /// trailing blanks trimmed per row. Empty cells render as space.
    /// </summary>
    public static string ScreenText(this TerminalBuffer buf)
    {
        var sb = new StringBuilder();
        for (int r = 0; r < buf.Rows; r++)
        {
            var row = buf.GetVisibleRow(r);
            var line = new StringBuilder();
            for (int c = 0; c < buf.Cols; c++)
            {
                if ((row[c].Flags2 & CellFlags2.IsContinuation) != 0) continue;
                int rn = row[c].Rune;
                line.Append(rn == 0 ? ' ' : char.ConvertFromUtf32(rn));
            }
            if (r > 0) sb.Append('\n');
            sb.Append(line.ToString().TrimEnd(' '));
        }
        return sb.ToString();
    }

    /// <summary>Text of row <paramref name="r"/>, trailing spaces stripped.</summary>
    public static string RowText(this TerminalBuffer buf, int r)
    {
        var row = buf.GetVisibleRow(r);
        var line = new StringBuilder();
        for (int c = 0; c < buf.Cols; c++)
        {
            if ((row[c].Flags2 & CellFlags2.IsContinuation) != 0) continue;
            int rn = row[c].Rune;
            line.Append(rn == 0 ? ' ' : char.ConvertFromUtf32(rn));
        }
        return line.ToString().TrimEnd(' ');
    }

    /// <summary>Text of row <paramref name="r"/>, left-padded with cols
    /// of spaces (no trimming). Useful for asserting cursor columns
    /// after control sequences.</summary>
    public static string RowTextRaw(this TerminalBuffer buf, int r)
    {
        var row = buf.GetVisibleRow(r);
        var line = new StringBuilder();
        for (int c = 0; c < buf.Cols; c++)
        {
            if ((row[c].Flags2 & CellFlags2.IsContinuation) != 0) continue;
            int rn = row[c].Rune;
            line.Append(rn == 0 ? ' ' : char.ConvertFromUtf32(rn));
        }
        return line.ToString();
    }

    /// <summary>Pull accumulated DSR/DA replies as an ASCII string.</summary>
    public static string TakeRepliesAscii(this TerminalBuffer buf)
    {
        var b = buf.TakeReplies();
        return b == null ? string.Empty : Encoding.ASCII.GetString(b);
    }

    /// <summary>ESC (\x1b) as a C# string literal shortcut.</summary>
    public const string ESC = "\x1b";

    /// <summary>CSI introducer.</summary>
    public const string CSI = "\x1b[";

    /// <summary>OSC introducer.</summary>
    public const string OSC = "\x1b]";

    /// <summary>String terminator (used to close OSC/DCS).</summary>
    public const string ST = "\x1b\\";
}
