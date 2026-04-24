using System;
using System.Collections.Generic;
using System.Text;
using Exclr8.Terminal.Render;

namespace Exclr8.Terminal.Buffer;

/// <summary>
/// Payload for OSC 52 (set clipboard) requests. The host decides
/// whether to honour the request based on trust level, so the event
/// surfaces the decoded text and the host picks its policy.
/// </summary>
public sealed class ClipboardRequestEventArgs : EventArgs
{
    public string Text { get; }
    public ClipboardRequestEventArgs(string text) { Text = text; }
}

/// <summary>
/// Handles OSC (Operating System Command) sequences — window title,
/// icon name, palette query/set, hyperlink framing (OSC 8), clipboard
/// (OSC 52), default colour query/set (OSC 10/11/12). Owns the
/// palette + hyperlink table + title string; raises events for the
/// host when titles change or a clipboard write is requested; replies
/// to the PTY via the provided callback for query sequences.
///
/// <para>Separated from <see cref="TerminalBuffer"/> so the string-
/// heavy OSC parsing and payload formatting don't clutter the cell
/// grid; the buffer holds a single instance and forwards events.</para>
/// </summary>
internal sealed class OscDispatcher
{
    private readonly Action<byte[]> _reply;

    private readonly Dictionary<ushort, string> _hyperlinks = new();
    private ushort _nextHyperlinkId = 1;
    private string _windowTitle = string.Empty;
    private uint[]? _palette256;

    /// <summary>Hyperlink id to apply to subsequent printed cells. 0
    /// means "no link". Set by OSC 8 open; cleared by OSC 8 close.</summary>
    public ushort ActiveLinkId { get; private set; }

    /// <summary>OSC 52 clipboard routing gate. Default off because a
    /// remote process can otherwise silently scrape the host clipboard.</summary>
    public bool AllowClipboardAccess { get; set; }

    public uint DefaultForegroundRgb { get; set; } = 0xD0D0D0;
    public uint DefaultBackgroundRgb { get; set; } = 0x1E1E1E;
    public uint DefaultCursorRgb     { get; set; } = 0xD0D0D0;

    /// <summary>OSC 0 or OSC 2 — window title.</summary>
    public event EventHandler<string>? TitleChanged;

    /// <summary>OSC 0 or OSC 1 — icon name. Most shells emit OSC 0
    /// which sets both title and icon name.</summary>
    public event EventHandler<string>? IconNameChanged;

    /// <summary>OSC 52 ; c ; base64 — decoded text the shell wants
    /// written to the host clipboard. Only fires when
    /// <see cref="AllowClipboardAccess"/> is true.</summary>
    public event EventHandler<ClipboardRequestEventArgs>? ClipboardRequested;

    public OscDispatcher(Action<byte[]> reply) { _reply = reply; }

    public bool TryGetHyperlink(ushort id, out string url) =>
        _hyperlinks.TryGetValue(id, out url!);

    /// <summary>Reset hyperlink + title state. Called from the
    /// buffer's RIS path. Palette / default-colour overrides are kept
    /// (matches xterm — RIS doesn't clear OSC 4/10/11/12 state).</summary>
    public void Reset()
    {
        _hyperlinks.Clear();
        _nextHyperlinkId = 1;
        ActiveLinkId = 0;
        _windowTitle = string.Empty;
    }

    public void Dispatch(string payload)
    {
        int semi = payload.IndexOf(';');
        if (semi < 0) return;
        if (!int.TryParse(payload.AsSpan(0, semi), out int cmd)) return;
        var data = payload.Substring(semi + 1);
        switch (cmd)
        {
            case 0:
                _windowTitle = data;
                TitleChanged?.Invoke(this, data);
                IconNameChanged?.Invoke(this, data);
                return;
            case 1:
                IconNameChanged?.Invoke(this, data);
                return;
            case 2:
                _windowTitle = data;
                TitleChanged?.Invoke(this, data);
                return;
            case 4:  HandleOsc4 (data); return;
            case 8:  HandleOsc8 (data); return;
            case 10: HandleOscSpecialColor(10, () => DefaultForegroundRgb, v => DefaultForegroundRgb = v, data); return;
            case 11: HandleOscSpecialColor(11, () => DefaultBackgroundRgb, v => DefaultBackgroundRgb = v, data); return;
            case 12: HandleOscSpecialColor(12, () => DefaultCursorRgb,     v => DefaultCursorRgb     = v, data); return;
            case 52: HandleOsc52(data); return;
        }
    }

    /// <summary>Reports the current window title via OSC l / OSC L
    /// (CSI 20/21 t queries). Package-internal because only
    /// <see cref="TerminalBuffer"/>'s CSI t handler needs it.</summary>
    internal string WindowTitle => _windowTitle;

    // ---- OSC 4: palette entry query / set ----

    private uint[] EnsurePalette256()
    {
        if (_palette256 != null) return _palette256;
        _palette256 = new uint[256];
        for (int i = 0; i < 256; i++)
        {
            var c = TerminalPalette.Indexed[i];
            _palette256[i] = (uint)((c.R << 16) | (c.G << 8) | c.B);
        }
        return _palette256;
    }

    private void HandleOsc4(string data)
    {
        // "idx;spec[;idx;spec...]". "?" = query, else parse + set.
        // Set path is maintained for query round-trip consistency;
        // note that mutations do NOT propagate to the renderer's
        // static palette — that's a broader refactor (see R7).
        var parts = data.Split(';');
        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            if (!int.TryParse(parts[i], out int idx) || idx < 0 || idx > 255) continue;
            var spec = parts[i + 1];
            if (spec == "?")
            {
                var pal = EnsurePalette256();
                ReplyAscii($"\x1b]4;{idx};{RgbSpec(pal[idx])}\x1b\\");
            }
            else if (TryParseRgbSpec(spec, out var rgb))
            {
                EnsurePalette256()[idx] = rgb;
            }
        }
    }

    private void HandleOscSpecialColor(int cmd, Func<uint> getter, Action<uint> setter, string data)
    {
        if (data == "?")
        {
            ReplyAscii($"\x1b]{cmd};{RgbSpec(getter())}\x1b\\");
        }
        else if (TryParseRgbSpec(data, out var rgb))
        {
            setter(rgb);
        }
    }

    // ---- OSC 8: hyperlink open / close ----

    private void HandleOsc8(string data)
    {
        // Payload is "params;URL". Empty URL closes the active link.
        int semi = data.IndexOf(';');
        if (semi < 0) { ActiveLinkId = 0; return; }
        string url = data.Substring(semi + 1);
        if (string.IsNullOrEmpty(url))
        {
            ActiveLinkId = 0;
        }
        else
        {
            ActiveLinkId = _nextHyperlinkId++;
            if (_nextHyperlinkId == 0) _nextHyperlinkId = 1;
            _hyperlinks[ActiveLinkId] = url;
        }
    }

    // ---- OSC 52: clipboard ----

    private void HandleOsc52(string data)
    {
        // "clipboards;payload". Payload is base64 for set or "?" for
        // get. We gate on AllowClipboardAccess. Get path ignored: the
        // host decides whether to leak clipboard contents back.
        if (!AllowClipboardAccess) return;
        int semi = data.IndexOf(';');
        if (semi < 0) return;
        var body = data.Substring(semi + 1);
        if (body == "?") return;
        string decoded;
        try
        {
            var bytes = Convert.FromBase64String(body);
            decoded = Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            return;
        }
        ClipboardRequested?.Invoke(this, new ClipboardRequestEventArgs(decoded));
    }

    // ---- RGB spec parsing / formatting ----

    private static string RgbSpec(uint rgb)
    {
        int r = (int)((rgb >> 16) & 0xFF);
        int g = (int)((rgb >>  8) & 0xFF);
        int b = (int)( rgb        & 0xFF);
        // xterm replies with 16-bit components; repeat the 8-bit value
        // in both halves (e.g. 0xAB → 0xABAB) for format parity.
        return $"rgb:{r:x2}{r:x2}/{g:x2}{g:x2}/{b:x2}{b:x2}";
    }

    private static bool TryParseRgbSpec(string spec, out uint rgb)
    {
        rgb = 0;
        if (spec.StartsWith('#') && (spec.Length == 7 || spec.Length == 13))
        {
            int step = spec.Length == 7 ? 2 : 4;
            if (!int.TryParse(spec.AsSpan(1,        2), System.Globalization.NumberStyles.HexNumber, null, out int r)) return false;
            if (!int.TryParse(spec.AsSpan(1+step,   2), System.Globalization.NumberStyles.HexNumber, null, out int g)) return false;
            if (!int.TryParse(spec.AsSpan(1+step*2, 2), System.Globalization.NumberStyles.HexNumber, null, out int b)) return false;
            rgb = (uint)((r << 16) | (g << 8) | b);
            return true;
        }
        if (spec.StartsWith("rgb:", StringComparison.Ordinal))
        {
            var parts = spec.Substring(4).Split('/');
            if (parts.Length != 3) return false;
            if (!TryTopByte(parts[0], out int r)) return false;
            if (!TryTopByte(parts[1], out int g)) return false;
            if (!TryTopByte(parts[2], out int b)) return false;
            rgb = (uint)((r << 16) | (g << 8) | b);
            return true;
        }
        return false;
    }

    private static bool TryTopByte(string hex, out int value)
    {
        value = 0;
        if (hex.Length == 0 || hex.Length > 4) return false;
        if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int raw)) return false;
        int scaled = hex.Length switch
        {
            1 => raw * 0x11,
            2 => raw,
            3 => (raw >> 4),
            4 => (raw >> 8),
            _ => raw,
        };
        value = scaled & 0xFF;
        return true;
    }

    private void ReplyAscii(string s) => _reply(Encoding.ASCII.GetBytes(s));
}
