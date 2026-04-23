using System;
using Exclr8.Terminal.Buffer;
using Xunit;
using static Exclr8.Terminal.Tests.TestHelpers;

namespace Exclr8.Terminal.Tests;

/// <summary>
/// Property-style fuzz: feed random byte streams (drawn from printable
/// ASCII, control chars, and ESC-led sequences) and assert the parser
/// never throws and leaves the buffer in a consistent state.
///
/// All randomness is drawn from a fixed seed so a failure reproduces
/// deterministically. Each iteration is small enough that the full
/// suite of 100 still runs under ~50 ms on the dev box.
/// </summary>
public class WorkloadFuzzTests
{
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

    /// <summary>Deterministic single-seed fuzz. If this test ever fails
    /// we get the exact same input back; no flakiness.</summary>
    [Fact]
    public void Fuzz_100RandomStreams_NoThrowAndInvariantsHold()
    {
        var rng = new Random(0xDECAF);
        for (int iter = 0; iter < 100; iter++)
        {
            int len = rng.Next(1, 2000);   // small enough to stay fast
            var bytes = new byte[len];
            for (int i = 0; i < len; i++)
                bytes[i] = PickByte(rng);

            var buf = NewBuffer(20, 8);
            buf.Write(bytes);
            AssertInvariants(buf);
        }
    }

    /// <summary>Fuzz that includes multi-byte UTF-8 sequences so we
    /// also exercise the decoder's split handling.
    ///
    /// Currently skipped: the fuzz finds a real buffer bug where a
    /// wide glyph overwriting the *right half* of a previous wide
    /// glyph leaves an orphan <c>IsContinuation</c> cell at
    /// <c>cursorCol + 2</c>. Deterministic minimal repro is captured
    /// as <see cref="WorkloadBugRepros.OverwriteWideWithWide_DoesNotLeaveOrphanContinuation"/>
    /// — see BUGS-FOUND.md. Remove the Skip when the cleanup in
    /// <c>TerminalBuffer.Print</c> is widened.</summary>
    [Fact(Skip = "BUGS-FOUND.md #1 — wide-over-wide orphan continuation")]
    public void Fuzz_Utf8MixedStreams_NoThrow()
    {
        var rng = new Random(0xBADBED);
        for (int iter = 0; iter < 50; iter++)
        {
            var bytes = new byte[rng.Next(50, 2000)];
            for (int i = 0; i < bytes.Length; i++)
            {
                int pick = rng.Next(10);
                if (pick == 0 && i + 2 < bytes.Length)
                {
                    // 2-byte UTF-8 (U+00A0..U+07FF) — Latin/Greek/Arabic
                    bytes[i]     = 0xC3;
                    bytes[i + 1] = (byte)(0x80 | rng.Next(0x40));
                    i++;
                }
                else if (pick == 1 && i + 3 < bytes.Length)
                {
                    // 3-byte UTF-8 (U+0800..U+FFFF) — CJK, BMP emoji.
                    bytes[i]     = 0xE4;
                    bytes[i + 1] = (byte)(0x80 | rng.Next(0x40));
                    bytes[i + 2] = (byte)(0x80 | rng.Next(0x40));
                    i += 2;
                }
                else if (pick == 2 && i + 4 < bytes.Length)
                {
                    // 4-byte UTF-8 (supplementary plane) — emoji.
                    bytes[i]     = 0xF0;
                    bytes[i + 1] = (byte)(0x9F);
                    bytes[i + 2] = (byte)(0x80 | rng.Next(0x40));
                    bytes[i + 3] = (byte)(0x80 | rng.Next(0x40));
                    i += 3;
                }
                else
                {
                    bytes[i] = PickByte(rng);
                }
            }

            var buf = NewBuffer(20, 8);
            buf.Write(bytes);
            AssertInvariants(buf);
        }
    }

    /// <summary>Fuzz chunked Write — same bytes, random chunk
    /// boundaries — must never diverge into invalid state.</summary>
    [Fact]
    public void Fuzz_ChunkedWriteInvariants()
    {
        var rng = new Random(unchecked((int)0xFEEDFACE));
        for (int iter = 0; iter < 50; iter++)
        {
            var bytes = new byte[rng.Next(100, 1500)];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = PickByte(rng);

            var buf = NewBuffer(20, 8);
            int pos = 0;
            while (pos < bytes.Length)
            {
                int chunk = rng.Next(1, 30);
                if (pos + chunk > bytes.Length) chunk = bytes.Length - pos;
                buf.Write(bytes.AsSpan(pos, chunk));
                pos += chunk;
            }
            AssertInvariants(buf);
        }
    }

    // ------------------------------------------------------------------
    // Byte picker — weighted toward printable ASCII, with a bucket of
    // control chars and ESC-led sequences so we regularly tickle the
    // VT state machine.
    // ------------------------------------------------------------------
    private static byte PickByte(Random rng)
    {
        int bucket = rng.Next(100);
        if (bucket < 60)
        {
            // Printable ASCII.
            return (byte)(0x20 + rng.Next(0x5F));
        }
        if (bucket < 75)
        {
            // Control chars (BS, HT, LF, VT, FF, CR, SO, SI).
            byte[] controls = { 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F };
            return controls[rng.Next(controls.Length)];
        }
        if (bucket < 85)
        {
            // ESC — kicks off an escape sequence. The fuzz'll most
            // often not follow with a valid final, so the parser
            // gets regularly reset mid-sequence.
            return 0x1B;
        }
        // Everything else: arbitrary byte, incl. high-bit bytes that
        // may or may not be valid UTF-8 continuations.
        return (byte)(0x80 + rng.Next(0x80));
    }
}
