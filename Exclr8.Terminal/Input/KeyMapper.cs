using System;
using System.Text;
using Avalonia.Input;

namespace Exclr8.Terminal.Input;

/// <summary>
/// Maps Avalonia <see cref="KeyEventArgs"/> to the byte sequences that
/// xterm-compatible terminals send over the PTY. Honors:
/// <list type="bullet">
///   <item>DECCKM (application cursor keys): arrows send SS3
///     (<c>ESC O A..D</c>) instead of CSI (<c>ESC [ A..D</c>) when
///     enabled.</item>
///   <item>DECKPAM (application keypad): numpad keys send their
///     SS3 equivalents (<c>ESC O p..y</c>, etc.) when enabled and no
///     modifiers are held.</item>
///   <item>Ctrl+letter → 0x01..0x1A, plus the usual Ctrl-symbol
///     mappings (Ctrl+@, Ctrl+[, Ctrl+\, Ctrl+], Ctrl+^, Ctrl+_).</item>
///   <item>Alt+char → ESC-prefix (the "meta sends ESC" xterm convention)
///     — applied in <see cref="MapTextInput"/>, not here.</item>
/// </list>
/// Returns an empty array for anything unhandled so the caller can fall
/// through to the TextInput event path.
/// </summary>
public static class KeyMapper
{
    public static byte[] Map(KeyEventArgs e, bool appCursorKeys = false, bool appKeypad = false,
        int modifyOtherKeys = 0)
        => Map(e.Key, e.KeyModifiers, appCursorKeys, appKeypad, modifyOtherKeys);

    /// <summary>Pure logical form of <see cref="Map(KeyEventArgs, bool, bool, int)"/> —
    /// same mapping, but takes the key and modifiers directly so it
    /// can be unit-tested without constructing an Avalonia
    /// <see cref="KeyEventArgs"/>. <paramref name="modifyOtherKeys"/>
    /// is the XTMODKEYS level (0/1/2) the host has set; level 2
    /// switches Ctrl/Shift/Alt + ASCII combinations to the unambiguous
    /// <c>CSI 27;mod;key~</c> form.</summary>
    public static byte[] Map(Key key, KeyModifiers mods, bool appCursorKeys = false, bool appKeypad = false,
        int modifyOtherKeys = 0)
    {
        bool ctrl  = (mods & KeyModifiers.Control) != 0;
        bool alt   = (mods & KeyModifiers.Alt)     != 0;
        bool shift = (mods & KeyModifiers.Shift)   != 0;
        bool meta  = (mods & KeyModifiers.Meta)    != 0;

        // Cmd/Meta alone is an app shortcut, not a terminal sequence.
        if (meta && !ctrl && !alt) return Array.Empty<byte>();

        // xterm modifier-parameter encoding. mod=1 is "no modifier";
        // anything else switches special keys from their base form
        // (CSI A, SS3 P, CSI 5 ~) to the modifier-carrying form
        // (CSI 1 ; mod A, CSI 1 ; mod P, CSI 5 ; mod ~) so vim / tmux
        // / readline can tell Ctrl+Up from Up.
        int mod = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0) + (meta ? 8 : 0);
        bool hasMod = mod > 1;

        // modifyOtherKeys level 2: Shift+Enter / Shift+Tab / Ctrl+Enter
        // / Ctrl+Tab / Ctrl+Backspace need the disambiguating CSI form.
        // Without it, Ctrl+Tab is indistinguishable from plain Tab.
        if (modifyOtherKeys >= 2 && hasMod)
        {
            int? code = key switch
            {
                Key.Enter  => 13,
                Key.Tab    => 9,
                Key.Back   => 127,
                Key.Escape => 27,
                Key.Space  => 32,
                _ => (int?)null,
            };
            if (code is int kc) return Esc($"[27;{mod};{kc}~");
        }

        switch (key)
        {
            case Key.Up:       return LetterKey('A', appCursorKeys, hasMod, mod);
            case Key.Down:     return LetterKey('B', appCursorKeys, hasMod, mod);
            case Key.Right:    return LetterKey('C', appCursorKeys, hasMod, mod);
            case Key.Left:     return LetterKey('D', appCursorKeys, hasMod, mod);
            case Key.Home:     return LetterKey('H', appCursorKeys, hasMod, mod);
            case Key.End:      return LetterKey('F', appCursorKeys, hasMod, mod);
            case Key.PageUp:   return TildeKey(5,  hasMod, mod);
            case Key.PageDown: return TildeKey(6,  hasMod, mod);
            case Key.Insert:   return TildeKey(2,  hasMod, mod);
            case Key.Delete:   return TildeKey(3,  hasMod, mod);
            // F1-F4 use SS3 in the unmodified form, CSI-letter with modifier.
            case Key.F1:       return Ss3OrModCsi('P', hasMod, mod);
            case Key.F2:       return Ss3OrModCsi('Q', hasMod, mod);
            case Key.F3:       return Ss3OrModCsi('R', hasMod, mod);
            case Key.F4:       return Ss3OrModCsi('S', hasMod, mod);
            // F5-F12 use the CSI tilde form; modifiers extend it.
            case Key.F5:       return TildeKey(15, hasMod, mod);
            case Key.F6:       return TildeKey(17, hasMod, mod);
            case Key.F7:       return TildeKey(18, hasMod, mod);
            case Key.F8:       return TildeKey(19, hasMod, mod);
            case Key.F9:       return TildeKey(20, hasMod, mod);
            case Key.F10:      return TildeKey(21, hasMod, mod);
            case Key.F11:      return TildeKey(23, hasMod, mod);
            case Key.F12:      return TildeKey(24, hasMod, mod);
            case Key.Enter:    return new byte[] { 0x0D };
            case Key.Tab:      return shift ? Esc("[Z") : new byte[] { 0x09 };
            // Backspace: DEL (0x7F) on every platform. Windows
            // Terminal, iTerm2, gnome-terminal, xterm all agree.
            // ConPTY on Windows translates 0x7F to VK_BACK without
            // modifiers; sending 0x08 (BS) would be interpreted as
            // Ctrl+H instead, which PSReadline maps to
            // BackwardKillWord — i.e. "Backspace deletes whole
            // words". Matches the one earlier symptom of a glyph
            // being printed in `cmd.exe`: that was the raw byte
            // being echoed by a program that wasn't doing line
            // editing, not a real Backspace-doesn't-work bug.
            case Key.Back:     return new byte[] { 0x7F };
            case Key.Escape:   return new byte[] { 0x1B };
            // Ctrl+Space is the standard "send NUL (0x00)" binding —
            // emacs uses it for set-mark, readline for
            // mark-or-nothing. Plain / Shift / Alt-only Space stays
            // as SP.
            case Key.Space:    return (ctrl && !alt)
                                   ? new byte[] { 0x00 }
                                   : new byte[] { 0x20 };
        }

        // DECKPAM: unmodified numpad keys send SS3 sequences.
        if (appKeypad && !ctrl && !alt && !shift)
        {
            var kp = MapNumpad(key);
            if (kp != null) return kp;
        }

        // Ctrl+A..Z → 0x01..0x1A.
        if (ctrl && !alt && key >= Key.A && key <= Key.Z)
        {
            // modifyOtherKeys level 2: every Ctrl/Shift+letter goes
            // through the unambiguous CSI form so Ctrl+Shift+letter
            // doesn't collide with plain Ctrl+letter.
            if (modifyOtherKeys >= 2 && (shift || meta))
            {
                int kc = (int)('a' + (key - Key.A));
                return Esc($"[27;{mod};{kc}~");
            }
            return new byte[] { (byte)(key - Key.A + 1) };
        }

        // Ctrl+symbol mappings.
        if (ctrl && !alt)
        {
            switch (key)
            {
                case Key.D2:       return new byte[] { 0x00 }; // Ctrl+@
                case Key.D6:       return new byte[] { 0x1E }; // Ctrl+^
                case Key.OemMinus: return new byte[] { 0x1F }; // Ctrl+_
            }
        }

        return Array.Empty<byte>();
    }

    // Arrows / Home / End. Unmodified respects DECCKM (SS3 when app-
    // cursor mode is on). Any non-trivial modifier forces CSI with
    // the modifier parameter, since SS3 has no encoding for it.
    private static byte[] LetterKey(char final, bool appCursor, bool hasMod, int mod)
    {
        if (hasMod) return Esc($"[1;{mod}{final}");
        return appCursor ? Esc($"O{final}") : Esc($"[{final}");
    }

    // Editing / function keys that use the CSI code~ form.
    private static byte[] TildeKey(int code, bool hasMod, int mod)
    {
        if (hasMod) return Esc($"[{code};{mod}~");
        return Esc($"[{code}~");
    }

    // F1-F4: SS3 when unmodified (the xterm classic), CSI letter with
    // modifier parameter otherwise. Apps that care — vim, tmux keybinds —
    // read both forms.
    private static byte[] Ss3OrModCsi(char final, bool hasMod, int mod)
    {
        if (hasMod) return Esc($"[1;{mod}{final}");
        return Esc($"O{final}");
    }

    private static byte[]? MapNumpad(Key key) => key switch
    {
        Key.NumPad0  => Esc("Op"), Key.NumPad1 => Esc("Oq"),
        Key.NumPad2  => Esc("Or"), Key.NumPad3 => Esc("Os"),
        Key.NumPad4  => Esc("Ot"), Key.NumPad5 => Esc("Ou"),
        Key.NumPad6  => Esc("Ov"), Key.NumPad7 => Esc("Ow"),
        Key.NumPad8  => Esc("Ox"), Key.NumPad9 => Esc("Oy"),
        Key.Decimal  => Esc("On"), Key.Add      => Esc("Ok"),
        Key.Subtract => Esc("Om"), Key.Multiply => Esc("Oj"),
        Key.Divide   => Esc("Oo"), _ => null,
    };

    /// <summary>
    /// Map TextInput. Alt+char is sent as ESC+char (the xterm "meta
    /// sends ESC" convention). Plain TextInput is UTF-8-encoded.
    /// </summary>
    public static byte[] MapTextInput(string text, bool altPressed)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<byte>();
        var bytes = Encoding.UTF8.GetBytes(text);
        if (!altPressed) return bytes;
        var esc = new byte[bytes.Length + 1];
        esc[0] = 0x1B;
        System.Buffer.BlockCopy(bytes, 0, esc, 1, bytes.Length);
        return esc;
    }

    private static byte[] Esc(string tail)
    {
        var b = Encoding.ASCII.GetBytes(tail);
        var r = new byte[b.Length + 1];
        r[0] = 0x1B;
        System.Buffer.BlockCopy(b, 0, r, 1, b.Length);
        return r;
    }
}
