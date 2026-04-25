using System;

namespace Exclr8.Terminal.Parser;

/// <summary>
/// User-supplied CSI handler. Returns <c>true</c> if the handler
/// consumed the dispatch (i.e. the built-in path should not run).
/// Returning <c>false</c> falls through to the next-registered
/// handler, then to the built-in TerminalBuffer dispatch.
/// </summary>
public delegate bool CsiHandler(ReadOnlySpan<int> parameters, string intermediates);

/// <summary>User-supplied OSC handler. <paramref name="data"/> is
/// the payload after <c>OSC &lt;n&gt; ;</c> (no leading number).</summary>
public delegate bool OscHandler(ReadOnlySpan<char> data);

/// <summary>User-supplied ESC handler. Used for ESC sequences with a
/// matching final byte + intermediates.</summary>
public delegate bool EscHandler(string intermediates);

/// <summary>User-supplied DCS handler. <paramref name="payload"/> is
/// the data between the framing and the ST terminator.</summary>
public delegate bool DcsHandler(ReadOnlySpan<int> parameters, string intermediates,
    ReadOnlySpan<char> payload);
