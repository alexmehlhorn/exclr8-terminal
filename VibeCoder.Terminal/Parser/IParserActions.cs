using System;

namespace VibeCoder.Terminal.Parser;

/// <summary>
/// Callback surface the VT parser invokes while walking its state
/// machine. Modeled on xterm.js's <c>InputHandler</c> interface: the
/// parser knows about sequence framing, the implementation knows what
/// the sequences mean. Keeps the state machine purely structural and
/// easy to test.
/// </summary>
public interface IParserActions
{
    /// <summary>A printable codepoint.</summary>
    void Print(int rune);

    /// <summary>A C0 control byte (0x00-0x1F, 0x7F).</summary>
    void Execute(byte c0);

    /// <summary>
    /// A CSI dispatch — <c>ESC [ params final</c>. <paramref name="intermediates"/>
    /// is any intermediate bytes (0x20-0x2F) between params and final
    /// (rare for most of what we care about).
    /// </summary>
    void CsiDispatch(char final, int[] parameters, string intermediates, char privatePrefix);

    /// <summary>An ESC dispatch — <c>ESC final</c> with possible intermediates.</summary>
    void EscDispatch(char final, string intermediates);

    /// <summary>An OSC dispatch — <c>ESC ] ... BEL</c>. The bytes
    /// between <c>]</c> and terminator are delivered as a string.</summary>
    void OscDispatch(string payload);

    /// <summary>
    /// Request the terminal write bytes back to the PTY. Used for DSR
    /// (cursor-position report) + DA (device attributes) responses.
    /// The implementation may buffer these and fire them once at the
    /// end of a parse run.
    /// </summary>
    void ReplyToPty(ReadOnlySpan<byte> bytes);
}
