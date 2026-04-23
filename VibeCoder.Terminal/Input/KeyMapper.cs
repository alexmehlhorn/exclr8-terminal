using System;
using System.Text;
using Avalonia.Input;

namespace VibeCoder.Terminal.Input;

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
    public static byte[] Map(KeyEventArgs e, bool appCursorKeys = false, bool appKeypad = false)
    {
        var mods  = e.KeyModifiers;
        bool ctrl  = (mods & KeyModifiers.Control) != 0;
        bool alt   = (mods & KeyModifiers.Alt)     != 0;
        bool shift = (mods & KeyModifiers.Shift)   != 0;
        bool meta  = (mods & KeyModifiers.Meta)    != 0;

        // Cmd/Meta alone is an app shortcut, not a terminal sequence.
        if (meta && !ctrl && !alt) return Array.Empty<byte>();

        // DECCKM: arrows flip between CSI and SS3. Home/End too.
        string ar = appCursorKeys ? "O" : "[";
        switch (e.Key)
        {
            case Key.Up:       return Esc(ar + "A");
            case Key.Down:     return Esc(ar + "B");
            case Key.Right:    return Esc(ar + "C");
            case Key.Left:     return Esc(ar + "D");
            case Key.Home:     return appCursorKeys ? Esc("OH") : Esc("[H");
            case Key.End:      return appCursorKeys ? Esc("OF") : Esc("[F");
            case Key.PageUp:   return Esc("[5~");
            case Key.PageDown: return Esc("[6~");
            case Key.Insert:   return Esc("[2~");
            case Key.Delete:   return Esc("[3~");
            case Key.F1:       return Esc("OP");
            case Key.F2:       return Esc("OQ");
            case Key.F3:       return Esc("OR");
            case Key.F4:       return Esc("OS");
            case Key.F5:       return Esc("[15~");
            case Key.F6:       return Esc("[17~");
            case Key.F7:       return Esc("[18~");
            case Key.F8:       return Esc("[19~");
            case Key.F9:       return Esc("[20~");
            case Key.F10:      return Esc("[21~");
            case Key.F11:      return Esc("[23~");
            case Key.F12:      return Esc("[24~");
            case Key.Enter:    return new byte[] { 0x0D };
            case Key.Tab:      return shift ? Esc("[Z") : new byte[] { 0x09 };
            case Key.Back:     return new byte[] { 0x7F };
            case Key.Escape:   return new byte[] { 0x1B };
            case Key.Space:    return new byte[] { 0x20 };
        }

        // DECKPAM: unmodified numpad keys send SS3 sequences.
        if (appKeypad && !ctrl && !alt && !shift)
        {
            var kp = MapNumpad(e.Key);
            if (kp != null) return kp;
        }

        // Ctrl+A..Z → 0x01..0x1A.
        if (ctrl && !alt && e.Key >= Key.A && e.Key <= Key.Z)
            return new byte[] { (byte)(e.Key - Key.A + 1) };

        // Ctrl+symbol mappings.
        if (ctrl && !alt)
        {
            switch (e.Key)
            {
                case Key.D2:       return new byte[] { 0x00 }; // Ctrl+@
                case Key.D6:       return new byte[] { 0x1E }; // Ctrl+^
                case Key.OemMinus: return new byte[] { 0x1F }; // Ctrl+_
            }
        }

        return Array.Empty<byte>();
    }

    /// <summary>Legacy overload for call sites that don't (yet) know
    /// about the app-mode flags.</summary>
    public static byte[] Map(KeyEventArgs e) => Map(e, false, false);

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
