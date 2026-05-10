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

/// <summary>FinalTerm/iTerm2 semantic-prompt marker raised when the
/// shell emits OSC 133. Hosts use these to draw "previous prompt"
/// jumps, command-status gutters, and AI-style command boundaries.
/// </summary>
public enum SemanticPromptKind
{
    /// <summary>OSC 133 ; A — start of prompt.</summary>
    PromptStart,
    /// <summary>OSC 133 ; B — end of prompt / start of input.</summary>
    PromptEnd,
    /// <summary>OSC 133 ; C — command starts executing.</summary>
    CommandStart,
    /// <summary>OSC 133 ; D — command finished. Optional exit code in
    /// <see cref="SemanticPromptEventArgs.ExitCode"/>.</summary>
    CommandEnd,
}

public sealed class SemanticPromptEventArgs : EventArgs
{
    public SemanticPromptKind Kind { get; }
    /// <summary>Exit code reported by OSC 133 ; D ; &lt;code&gt;. Null if
    /// the kind is anything else or no code was supplied.</summary>
    public int? ExitCode { get; }
    public SemanticPromptEventArgs(SemanticPromptKind kind, int? exitCode = null)
    {
        Kind = kind;
        ExitCode = exitCode;
    }
}

/// <summary>ConEmu / Windows Terminal taskbar-progress states reported
/// via <c>OSC 9 ; 4 ; state ; pct ST</c>. Hosts surface these as
/// taskbar overlays or dock badges.</summary>
public enum ProgressState
{
    /// <summary>0 — remove the progress indicator.</summary>
    Remove,
    /// <summary>1 — normal in-progress (with a percentage).</summary>
    Normal,
    /// <summary>2 — error / failed (red).</summary>
    Error,
    /// <summary>3 — indeterminate (no percentage; spinner).</summary>
    Indeterminate,
    /// <summary>4 — warning / paused (yellow).</summary>
    Warning,
}

public sealed class ProgressEventArgs : EventArgs
{
    public ProgressState State { get; }
    /// <summary>0..100 percentage when reported; null for Remove or
    /// Indeterminate states.</summary>
    public int? Percent { get; }
    public ProgressEventArgs(ProgressState state, int? percent = null)
    {
        State = state;
        Percent = percent;
    }
}

/// <summary>
/// Handles OSC (Operating System Command) sequences — window title,
/// icon name, palette query/set, hyperlink framing (OSC 8), clipboard
/// (OSC 52), default colour query/set (OSC 10/11/12). Owns the
/// palette + hyperlink table + title string; raises events for the
/// host when titles change or a clipboard write is requested; replies
/// to the PTY via the provided callback for query sequences.
///
/// <para>The whole dispatch path is span-based: <see cref="Dispatch"/>
/// receives a <c>ReadOnlySpan&lt;char&gt;</c> that points into the
/// parser's internal buffer and is valid only for the call. We only
/// materialise strings at the *storage* sites — the window title,
/// hyperlink URLs, decoded OSC 52 clipboard text.</para>
/// </summary>
internal sealed class OscDispatcher
{
    private readonly Action<byte[]> _reply;

    private readonly Dictionary<ushort, string> _hyperlinks = new();
    private ushort _nextHyperlinkId = 1;

    /// <summary>
    /// Fires immediately before <see cref="_nextHyperlinkId"/> wraps
    /// past 65535 and the dictionary is cleared. Subscribers (the
    /// owning <see cref="TerminalBuffer"/>) walk every cell — primary
    /// screen, primary scrollback, alternate screen — and zero any
    /// non-zero <c>HyperlinkId</c>, so the recycled id slots cannot
    /// silently rebind existing on-screen cells to whatever URL the
    /// next OSC 8 sequence emits.
    /// </summary>
    public event Action? HyperlinkIdsRecycled;
    private string _windowTitle = string.Empty;
    private string _iconName    = string.Empty;
    private uint[]? _palette256;
    private bool[]? _paletteSet;

    /// <summary>Hyperlink id to apply to subsequent printed cells. 0
    /// means "no link". Set by OSC 8 open; cleared by OSC 8 close.</summary>
    public ushort ActiveLinkId { get; private set; }

    /// <summary>OSC 52 clipboard routing gate. Default off because a
    /// remote process can otherwise silently scrape the host clipboard.</summary>
    public bool AllowClipboardAccess { get; set; }

    private uint _defaultForegroundRgb = 0xD0D0D0;
    private uint _defaultBackgroundRgb = 0x1E1E1E;
    private uint _defaultCursorRgb     = 0xD0D0D0;

    public uint DefaultForegroundRgb
    {
        get => _defaultForegroundRgb;
        set { _defaultForegroundRgb = value; DefaultForegroundExplicit = true; }
    }
    public uint DefaultBackgroundRgb
    {
        get => _defaultBackgroundRgb;
        set { _defaultBackgroundRgb = value; DefaultBackgroundExplicit = true; }
    }
    public uint DefaultCursorRgb
    {
        get => _defaultCursorRgb;
        set { _defaultCursorRgb = value; DefaultCursorExplicit = true; }
    }

    /// <summary>True once the shell has explicitly set the default
    /// foreground via OSC 10. Until then, the renderer should fall
    /// back to the host theme / static palette default. Without this
    /// gate the renderer would always prefer our pre-seeded value
    /// and a host-supplied theme would never apply.</summary>
    public bool DefaultForegroundExplicit { get; private set; }
    public bool DefaultBackgroundExplicit { get; private set; }
    public bool DefaultCursorExplicit     { get; private set; }

    /// <summary>Live palette overrides driven by OSC 4. Returns true
    /// and sets <paramref name="rgb"/> when the shell has explicitly
    /// changed entry <paramref name="index"/>. Otherwise the renderer
    /// should consult the host theme and the static palette.</summary>
    public bool TryGetPaletteColor(int index, out uint rgb)
    {
        rgb = 0;
        if (_paletteSet == null) return false;
        if ((uint)index >= 256) return false;
        if (!_paletteSet[index]) return false;
        rgb = _palette256![index];
        return true;
    }

    /// <summary>OSC 0 or OSC 2 — window title.</summary>
    public event EventHandler<string>? TitleChanged;

    /// <summary>OSC 0 or OSC 1 — icon name. Most shells emit OSC 0
    /// which sets both title and icon name.</summary>
    public event EventHandler<string>? IconNameChanged;

    /// <summary>OSC 52 ; c ; base64 — decoded text the shell wants
    /// written to the host clipboard. Only fires when
    /// <see cref="AllowClipboardAccess"/> is true.</summary>
    public event EventHandler<ClipboardRequestEventArgs>? ClipboardRequested;

    /// <summary>Working directory the shell announced via OSC 7. The
    /// payload is `file://host/path` style; we strip the URL framing
    /// and surface the raw path. Useful for "open new tab here" UX.
    /// </summary>
    public string? WorkingDirectory { get; private set; }

    public event EventHandler<string>? WorkingDirectoryChanged;

    /// <summary>OSC 133 — FinalTerm/iTerm2 semantic prompts. Fired with
    /// kind A/B/C/D and an optional exit code on D.</summary>
    public event EventHandler<SemanticPromptEventArgs>? SemanticPrompt;

    /// <summary>OSC 9 ; 4 — ConEmu/Windows-Terminal task progress.
    /// Hosts surface as taskbar overlay or dock badge; useful pair
    /// with OSC 133 for build/test/install command tracking.</summary>
    public event EventHandler<ProgressEventArgs>? ProgressChanged;

    public OscDispatcher(Action<byte[]> reply) { _reply = reply; }

    public bool TryGetHyperlink(ushort id, out string url) =>
        _hyperlinks.TryGetValue(id, out url!);

    /// <summary>Force-clear the active OSC 8 hyperlink. The shell
    /// closes a hyperlink with <c>OSC 8 ; ; ST</c>, but if that close
    /// sequence is dropped en route (e.g. because the byte stream
    /// passed through a chat renderer that mangled escapes) the
    /// active link gets stuck and every subsequent printed cell
    /// inherits the underline. Hosts wire this to a "reset
    /// formatting" menu item / shortcut as a recovery path.</summary>
    public void ClearActiveHyperlink() => ActiveLinkId = 0;

    /// <summary>Reset hyperlink + title state. Called from the
    /// buffer's RIS path. Palette / default-colour overrides are kept
    /// (matches xterm — RIS doesn't clear OSC 4/10/11/12 state).</summary>
    public void Reset()
    {
        _hyperlinks.Clear();
        _nextHyperlinkId = 1;
        ActiveLinkId = 0;
        _windowTitle = string.Empty;
        _iconName    = string.Empty;
    }

    /// <summary>Reset palette + default colours. OSC 104 / 110 / 111 /
    /// 112 (hard palette resets) and full RIS via Reset can call this
    /// when a stronger reset is wanted than the default Reset.</summary>
    public void ResetPalette()
    {
        _palette256 = null;
        _paletteSet = null;
        DefaultForegroundExplicit = false;
        DefaultBackgroundExplicit = false;
        DefaultCursorExplicit     = false;
        PaletteChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispatch(ReadOnlySpan<char> payload)
    {
        int semi = payload.IndexOf(';');
        if (semi < 0)
        {
            // Some commands (notably 133 ; A and 133 ; B from minimal
            // implementations) are emitted without a body. The number
            // alone is the whole payload. Handle the no-body forms
            // explicitly rather than dropping them.
            if (int.TryParse(payload, out int bare)) DispatchNoBody(bare);
            return;
        }
        if (!int.TryParse(payload[..semi], out int cmd)) return;
        var data = payload[(semi + 1)..];
        switch (cmd)
        {
            case 0:
            {
                // OSC 0 sets both window title and icon name. zsh /
                // bash / fish prompt-frameworks emit OSC 0 on every
                // prompt redraw with the same string; we dedupe to
                // avoid a per-prompt string allocation + an event-
                // chain fire-out to subscribers that would just
                // compare-equal-and-no-op anyway.
                if (data.SequenceEqual(_windowTitle.AsSpan())
                 && data.SequenceEqual(_iconName.AsSpan()))
                    return;
                var s = new string(data);
                _windowTitle = s;
                _iconName = s;
                TitleChanged?.Invoke(this, s);
                IconNameChanged?.Invoke(this, s);
                return;
            }
            case 1:
            {
                if (data.SequenceEqual(_iconName.AsSpan())) return;
                var s = new string(data);
                _iconName = s;
                IconNameChanged?.Invoke(this, s);
                return;
            }
            case 2:
            {
                if (data.SequenceEqual(_windowTitle.AsSpan())) return;
                var s = new string(data);
                _windowTitle = s;
                TitleChanged?.Invoke(this, s);
                return;
            }
            case 4:  HandleOsc4 (data); return;
            case 7:  HandleOsc7 (data); return;
            case 8:  HandleOsc8 (data); return;
            case 9:  HandleOsc9 (data); return;
            case 10: HandleOscSpecialColor(10, DefaultForegroundRgb, data, v => DefaultForegroundRgb = v); return;
            case 11: HandleOscSpecialColor(11, DefaultBackgroundRgb, data, v => DefaultBackgroundRgb = v); return;
            case 12: HandleOscSpecialColor(12, DefaultCursorRgb,     data, v => DefaultCursorRgb     = v); return;
            case 52: HandleOsc52(data); return;
            case 133: HandleOsc133(data); return;
        }
        TerminalLog.TraceProtocol($"unhandled OSC: {cmd}");
    }

    private void DispatchNoBody(int cmd)
    {
        // Only OSC 133 needs a no-body path today; treat the bare
        // number as a generic command-end with no exit code.
        if (cmd == 133) { /* missing kind selector — ignore */ return; }
        TerminalLog.TraceProtocol($"unhandled OSC (no body): {cmd}");
    }

    // ---- OSC 7: working directory ----

    private void HandleOsc7(ReadOnlySpan<char> data)
    {
        // Payload is typically file://hostname/encoded/path. Strip the
        // scheme/host and percent-decode the path. Bare paths (some
        // shells don't bother with the file:// prefix) pass through.
        var path = data;
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            path = path[7..];
            int slash = path.IndexOf('/');
            // file://host/... — drop the host component.
            if (slash >= 0) path = path[slash..];
            else            path = default;
        }
        if (path.IsEmpty) return;
        string decoded = PercentDecode(path);
        if (decoded == WorkingDirectory) return;
        WorkingDirectory = decoded;
        WorkingDirectoryChanged?.Invoke(this, decoded);
    }

    private static string PercentDecode(ReadOnlySpan<char> input)
    {
        // Quick path: nothing to decode.
        if (input.IndexOf('%') < 0) return new string(input);

        // Per RFC 3986, percent-encoded octets in URIs decode to
        // *bytes*, and the conventional encoding shells use for OSC 7
        // paths is UTF-8. The earlier implementation appended each
        // %xx straight into the StringBuilder as a single UTF-16
        // code unit — that turned `%C3%A9` (UTF-8 for `é`) into the
        // mojibake pair `Ã©`. Decode into a byte buffer first, then
        // run the whole thing through UTF-8 to recover real glyphs.
        // Non-`%` characters from the input are themselves UTF-16
        // chars; encode them as UTF-8 into the same buffer so the
        // final UTF-8 decode is uniform.
        var utf8  = System.Text.Encoding.UTF8;
        var bytes = new byte[utf8.GetMaxByteCount(input.Length)];
        int n = 0;
        int runStart = -1; // start of an unbroken non-% run; -1 = no run open

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            bool isEscape =
                c == '%' && i + 2 < input.Length
                && IsHex(input[i + 1]) && IsHex(input[i + 2]);

            if (isEscape)
            {
                if (runStart >= 0)
                {
                    n += utf8.GetBytes(input.Slice(runStart, i - runStart), bytes.AsSpan(n));
                    runStart = -1;
                }
                int hi = HexVal(input[i + 1]);
                int lo = HexVal(input[i + 2]);
                bytes[n++] = (byte)((hi << 4) | lo);
                i += 2;
            }
            else if (runStart < 0)
            {
                runStart = i;
            }
        }
        if (runStart >= 0)
            n += utf8.GetBytes(input[runStart..], bytes.AsSpan(n));

        return utf8.GetString(bytes, 0, n);
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    private static int HexVal(char c) =>
        c <= '9' ? c - '0' : (c & 0x5F) - 'A' + 10;

    // ---- OSC 9: notifications + progress ----

    private void HandleOsc9(ReadOnlySpan<char> data)
    {
        // ConEmu progress form: "4;<state>[;<pct>]". Other OSC 9 forms
        // (notification text) we don't surface yet — silently drop.
        if (data.Length < 2 || data[0] != '4' || data[1] != ';') return;
        var rest = data[2..];
        int semi = rest.IndexOf(';');
        ReadOnlySpan<char> stateSpan, pctSpan;
        if (semi < 0) { stateSpan = rest; pctSpan = default; }
        else          { stateSpan = rest[..semi]; pctSpan = rest[(semi + 1)..]; }
        if (!int.TryParse(stateSpan, out int stateInt)) return;

        ProgressState state = stateInt switch
        {
            0 => ProgressState.Remove,
            1 => ProgressState.Normal,
            2 => ProgressState.Error,
            3 => ProgressState.Indeterminate,
            4 => ProgressState.Warning,
            _ => ProgressState.Remove,
        };
        int? pct = null;
        if (state is ProgressState.Normal or ProgressState.Error or ProgressState.Warning
            && !pctSpan.IsEmpty
            && int.TryParse(pctSpan, out int p))
        {
            pct = Math.Clamp(p, 0, 100);
        }
        ProgressChanged?.Invoke(this, new ProgressEventArgs(state, pct));
    }

    // ---- OSC 133: FinalTerm/iTerm2 semantic prompts ----

    private void HandleOsc133(ReadOnlySpan<char> data)
    {
        // Payload is "<kind>[;extra...]" where kind is A/B/C/D. We only
        // surface the kind selector — extra fields (e.g. "aid=...") are
        // command-line metadata that hosts that need it can parse on top.
        if (data.IsEmpty) return;
        char kind = data[0];
        SemanticPromptKind k;
        int? exitCode = null;
        switch (kind)
        {
            case 'A': k = SemanticPromptKind.PromptStart; break;
            case 'B': k = SemanticPromptKind.PromptEnd; break;
            case 'C': k = SemanticPromptKind.CommandStart; break;
            case 'D':
                k = SemanticPromptKind.CommandEnd;
                // OSC 133 ; D ; <exit code>
                if (data.Length > 2 && data[1] == ';')
                {
                    var rest = data[2..];
                    int semi = rest.IndexOf(';');
                    if (semi >= 0) rest = rest[..semi];
                    if (int.TryParse(rest, out int code)) exitCode = code;
                }
                break;
            default: return;
        }
        SemanticPrompt?.Invoke(this, new SemanticPromptEventArgs(k, exitCode));
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
        _paletteSet = new bool[256];
        for (int i = 0; i < 256; i++)
        {
            var c = TerminalPalette.Indexed[i];
            _palette256[i] = (uint)((c.R << 16) | (c.G << 8) | c.B);
        }
        return _palette256;
    }

    private void HandleOsc4(ReadOnlySpan<char> data)
    {
        // "idx;spec[;idx;spec...]". "?" = query, else parse + set.
        while (!data.IsEmpty)
        {
            var idxSpan = TakeToken(ref data);
            if (data.IsEmpty && idxSpan.IsEmpty) break;
            var spec    = TakeToken(ref data);
            if (!int.TryParse(idxSpan, out int idx) || idx < 0 || idx > 255) continue;
            if (spec.Length == 1 && spec[0] == '?')
            {
                var pal = EnsurePalette256();
                ReplyAscii($"\x1b]4;{idx};{RgbSpec(pal[idx])}\x1b\\");
            }
            else if (TryParseRgbSpec(spec, out var rgb))
            {
                EnsurePalette256()[idx] = rgb;
                _paletteSet![idx] = true;
                PaletteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>OSC 4 / 10 / 11 / 12 mutation. Renderer subscribes
    /// to repaint when the shell changes a palette entry or default
    /// colour at runtime.</summary>
    public event EventHandler? PaletteChanged;

    private void HandleOscSpecialColor(int cmd, uint currentValue,
        ReadOnlySpan<char> data, Action<uint> setter)
    {
        if (data.Length == 1 && data[0] == '?')
        {
            ReplyAscii($"\x1b]{cmd};{RgbSpec(currentValue)}\x1b\\");
        }
        else if (TryParseRgbSpec(data, out var rgb))
        {
            setter(rgb);
            PaletteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---- OSC 8: hyperlink open / close ----

    private void HandleOsc8(ReadOnlySpan<char> data)
    {
        // Payload is "params;URL". Empty URL closes the active link.
        int semi = data.IndexOf(';');
        if (semi < 0) { ActiveLinkId = 0; return; }
        var urlSpan = data[(semi + 1)..];
        if (urlSpan.IsEmpty)
        {
            ActiveLinkId = 0;
        }
        else
        {
            // URL gets retained — single string allocation here is
            // unavoidable.
            //
            // Wrap-and-recycle: with a ushort id we exhaust the space
            // after 65535 distinct OSC 8 emissions. If we just kept
            // assigning, the next sequence would land on id 1 and
            // _hyperlinks[1] = newUrl would silently rebind every
            // already-on-screen cell that points at id 1 to the new
            // URL — a click on an old visible link opens something
            // unrelated, and a hostile producer can deliberately
            // exhaust ids to redirect a freshly emitted "https://..."
            // onto a slot the user has been looking at. Before the
            // recycle, drop the dictionary AND ask the buffer to walk
            // every cell and zero any HyperlinkId so no on-screen cell
            // can accidentally resolve to a recycled slot. Old links
            // become non-clickable rather than misdirected.
            if (_nextHyperlinkId == 0)
            {
                HyperlinkIdsRecycled?.Invoke();
                _hyperlinks.Clear();
                _nextHyperlinkId = 1;
            }
            ActiveLinkId = _nextHyperlinkId++;
            _hyperlinks[ActiveLinkId] = new string(urlSpan);
        }
    }

    // ---- OSC 52: clipboard ----

    private void HandleOsc52(ReadOnlySpan<char> data)
    {
        // "clipboards;payload". Payload is base64 for set or "?" for
        // get. We gate on AllowClipboardAccess. Get path ignored: the
        // host decides whether to leak clipboard contents back.
        if (!AllowClipboardAccess) return;
        int semi = data.IndexOf(';');
        if (semi < 0) return;
        var body = data[(semi + 1)..];
        if (body.Length == 1 && body[0] == '?') return;

        // Decode base64 directly from the char span into a pooled /
        // stackalloc byte buffer, then UTF-8-decode to the event's
        // payload string. That string's the one retained allocation —
        // no intermediate base64-string copy.
        string decoded;
        try
        {
            byte[]? rented = null;
            Span<byte> bytes = body.Length <= 2048
                ? stackalloc byte[2048]
                : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(body.Length));
            bytes = bytes[..body.Length];
            if (!Convert.TryFromBase64Chars(body, bytes, out int written))
            {
                if (rented != null) System.Buffers.ArrayPool<byte>.Shared.Return(rented);
                return;
            }
            decoded = Encoding.UTF8.GetString(bytes[..written]);
            if (rented != null) System.Buffers.ArrayPool<byte>.Shared.Return(rented);
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

    private static bool TryParseRgbSpec(ReadOnlySpan<char> spec, out uint rgb)
    {
        rgb = 0;
        if (spec.Length > 0 && spec[0] == '#' && (spec.Length == 7 || spec.Length == 13))
        {
            int step = spec.Length == 7 ? 2 : 4;
            if (!int.TryParse(spec.Slice(1,        2), System.Globalization.NumberStyles.HexNumber, null, out int r)) return false;
            if (!int.TryParse(spec.Slice(1+step,   2), System.Globalization.NumberStyles.HexNumber, null, out int g)) return false;
            if (!int.TryParse(spec.Slice(1+step*2, 2), System.Globalization.NumberStyles.HexNumber, null, out int b)) return false;
            rgb = (uint)((r << 16) | (g << 8) | b);
            return true;
        }
        if (spec.StartsWith("rgb:", StringComparison.Ordinal))
        {
            var rest = spec[4..];
            int slash1 = rest.IndexOf('/');
            if (slash1 < 0) return false;
            var rr = rest[..slash1];
            rest = rest[(slash1 + 1)..];
            int slash2 = rest.IndexOf('/');
            if (slash2 < 0) return false;
            var gg = rest[..slash2];
            var bb = rest[(slash2 + 1)..];
            if (!TryTopByte(rr, out int r)) return false;
            if (!TryTopByte(gg, out int g)) return false;
            if (!TryTopByte(bb, out int b)) return false;
            rgb = (uint)((r << 16) | (g << 8) | b);
            return true;
        }
        return false;
    }

    private static bool TryTopByte(ReadOnlySpan<char> hex, out int value)
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

    /// <summary>Consume the next ';'-delimited token from <paramref name="data"/>
    /// and advance the slice past it. Leaves the separator out of the
    /// returned span. Returns the remainder (no separator) when there
    /// is no further ';'.</summary>
    private static ReadOnlySpan<char> TakeToken(ref ReadOnlySpan<char> data)
    {
        int i = data.IndexOf(';');
        if (i < 0)
        {
            var all = data;
            data = default;
            return all;
        }
        var tok = data[..i];
        data = data[(i + 1)..];
        return tok;
    }

    private void ReplyAscii(string s) => _reply(Encoding.ASCII.GetBytes(s));
}
