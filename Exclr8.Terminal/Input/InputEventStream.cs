using System;
using System.Text;

namespace Exclr8.Terminal.Input;

/// <summary>How a line arrived in the input stream — subscribers
/// filter on this when the distinction matters (history recorders
/// usually skip paste, destructive-command guards want to see
/// every origin, etc.).</summary>
public enum InputLineOrigin
{
    /// <summary>Typed one keystroke at a time.</summary>
    Typed,
    /// <summary>Landed via clipboard paste.</summary>
    Pasted,
    /// <summary>Replayed from shell history (Ctrl+R, arrow-up).
    /// Detection is best-effort today — see <c>InputEventStream</c>
    /// notes.</summary>
    Recalled,
    /// <summary>Emitted by VibeCoder itself (e.g. the selection-delete
    /// backspace burst), not a human action.</summary>
    Programmatic,
}

/// <summary>A single committed line of input. <see cref="FirstWord"/>
/// is pre-extracted as a convenience so common consumers
/// (badge, Claude watcher, dangerous-command guard) don't each
/// repeat the same split.</summary>
public readonly record struct InputLineEvent(
    string Line,
    string? FirstWord,
    InputLineOrigin Origin);

/// <summary>
/// A local observer of user input flowing through the terminal. The
/// terminal writes bytes to the PTY via the <c>Input</c> event; this
/// stream reads the same bytes on the way past and assembles them
/// into lines, exposing higher-level events that subscribers can
/// pattern-match without each reimplementing the byte-level state
/// machine.
///
/// Deliberately NOT shell-aware — events describe what the user
/// submitted, not what any particular shell will do with it.
/// Per-shell classification (is "cd" a builtin here? what counts
/// as a destructive command?) is subscriber-side concern.
/// </summary>
/// <remarks>
/// Limitations to be aware of when writing subscribers:
/// <list type="bullet">
///   <item><description>Ctrl+R history recall replaces the visible
///     input atomically — we cannot see that from the byte stream.
///     <see cref="InputLineOrigin.Recalled"/> is assigned
///     heuristically when we see a recall escape sequence pass
///     through; otherwise the line is reported as
///     <see cref="InputLineOrigin.Typed"/>.</description></item>
///   <item><description>Line-editor movements (arrow keys, Home,
///     End, Alt+B/F) don't rewrite our line buffer — the buffer
///     stays a best-effort of what the user appears to have
///     assembled. For slash-command / first-word recognition this
///     is fine; for exact reconstruction of long interactive
///     edits it is not.</description></item>
///   <item><description>The stream sees only input <i>outgoing</i>
///     from VibeCoder. Commands entered in another terminal are
///     invisible by construction.</description></item>
/// </list>
/// </remarks>
public sealed class InputEventStream
{
    private readonly StringBuilder _line = new();
    private InputLineOrigin _currentOrigin = InputLineOrigin.Typed;

    // Cap the line buffer so a malicious or runaway paste can't
    // grow it unbounded. 64 KiB is well past any human-typed line
    // and also past the 10 MiB paste cap's per-line expectations.
    private const int MaxLineLength = 64 * 1024;

    /// <summary>A full line of input has been committed (the user
    /// pressed Enter). Subscribers may fan out into any number of
    /// reactive behaviours.</summary>
    public event Action<InputLineEvent>? LineCommitted;

    /// <summary>Every input batch, before line assembly. For
    /// subscribers that need key-combo or escape-sequence awareness
    /// (Ctrl+R, Ctrl+D, function keys, …).</summary>
    public event Action<ReadOnlyMemory<byte>>? RawBytes;

    /// <summary>Feed a batch of user-input bytes through the
    /// stream. Caller is responsible for the <see cref="InputLineOrigin"/>
    /// classification; the stream trusts what it's told.</summary>
    public void Feed(ReadOnlyMemory<byte> payload, InputLineOrigin origin)
    {
        if (payload.IsEmpty) return;
        RawBytes?.Invoke(payload);
        _currentOrigin = origin;

        var span = payload.Span;
        for (int i = 0; i < span.Length; i++)
        {
            byte b = span[i];
            if (b == 0x0D || b == 0x0A)
            {
                Commit();
            }
            else if (b == 0x7F || b == 0x08)
            {
                if (_line.Length > 0) _line.Length--;
            }
            else if (b == 0x03 || b == 0x18 || b == 0x04)
            {
                // Ctrl+C / Ctrl+X / Ctrl+D all abort the in-progress
                // line from our perspective. Ctrl+D at an empty line
                // sends EOF to the shell; at a partial line, shells
                // differ (some forward-delete, some no-op) — either
                // way the line as we'd reconstruct it is invalidated,
                // so clear the buffer.
                _line.Clear();
            }
            else if (b == 0x1B)
            {
                // ESC-led sequences (arrows, Home/End, Alt-chords,
                // function keys, OSC/CSI). We don't try to rewrite
                // the line for line-editor motion; we just skip
                // past the escape's bytes so they don't pollute
                // the buffer. Conservative scan: consume the ESC,
                // an optional [ or O, then one final byte.
                int j = i + 1;
                if (j < span.Length && (span[j] == 0x5B || span[j] == 0x4F))
                    j++;
                while (j < span.Length && (span[j] < 0x40 || span[j] > 0x7E))
                    j++;
                if (j < span.Length) j++; // final byte
                i = j - 1; // -1 because outer loop ++
            }
            else if (b >= 0x20 && b < 0x7F)
            {
                if (_line.Length < MaxLineLength) _line.Append((char)b);
            }
            // Anything else (non-ASCII UTF-8 continuation bytes,
            // control chars we don't care about) is ignored for
            // line-assembly. UTF-8 decoding for the buffer could
            // be added later if subscribers need international
            // text in FirstWord — nobody does today.
        }
    }

    private void Commit()
    {
        var text = _line.ToString();
        _line.Clear();
        if (text.Length == 0) return;
        LineCommitted?.Invoke(new InputLineEvent(
            Line:      text,
            FirstWord: ExtractFirstWord(text),
            Origin:    _currentOrigin));
    }

    private static string? ExtractFirstWord(string line)
    {
        int start = 0;
        while (start < line.Length && char.IsWhiteSpace(line[start])) start++;
        if (start >= line.Length) return null;
        int end = start;
        while (end < line.Length && !char.IsWhiteSpace(line[end])) end++;
        return line[start..end];
    }
}
