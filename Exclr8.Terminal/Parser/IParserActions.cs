using System;

namespace Exclr8.Terminal.Parser;

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
    /// (rare for most of what we care about). The parameter span points
    /// into the parser's internal buffer and is valid only for the
    /// duration of the call — implementations must not store it.
    /// </summary>
    void CsiDispatch(char final, ReadOnlySpan<int> parameters, string intermediates, char privatePrefix);

    /// <summary>
    /// CSI dispatch with parallel sub-params (SGR 4:3 = curly
    /// underline). Slot i in <paramref name="subParameters"/> holds
    /// the colon-sub-parameter attached to <c>parameters[i]</c>, or 0
    /// if none. The default forwards to the simpler overload —
    /// implementations that care about sub-params override this and
    /// ignore the four-arg form.
    /// </summary>
    void CsiDispatchWithSub(char final, ReadOnlySpan<int> parameters,
        ReadOnlySpan<int> subParameters, string intermediates, char privatePrefix)
        => CsiDispatch(final, parameters, intermediates, privatePrefix);

    /// <summary>An ESC dispatch — <c>ESC final</c> with possible intermediates.</summary>
    void EscDispatch(char final, string intermediates);

    /// <summary>An OSC dispatch — <c>ESC ] ... BEL</c>. The payload
    /// span points into the parser's internal buffer and is valid
    /// only for the duration of the call. Implementations that need
    /// to retain the payload (window title, OSC 8 URL, clipboard
    /// text) must materialise their own string.</summary>
    void OscDispatch(ReadOnlySpan<char> payload);

    /// <summary>A DCS dispatch — <c>DCS params intermediates final
    /// payload ST</c>. Both <paramref name="parameters"/> and
    /// <paramref name="payload"/> point into parser-owned buffers;
    /// implementations must not retain them past the call.</summary>
    void DcsDispatch(char final, ReadOnlySpan<int> parameters, string intermediates,
        char privatePrefix, ReadOnlySpan<char> payload);

    /// <summary>
    /// Request the terminal write bytes back to the PTY. Used for DSR
    /// (cursor-position report) + DA (device attributes) responses.
    /// The implementation may buffer these and fire them once at the
    /// end of a parse run.
    /// </summary>
    void ReplyToPty(ReadOnlySpan<byte> bytes);
}
