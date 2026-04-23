using System;
using System.Text;
using Avalonia.Input;

namespace VibeCoder.Terminal.Input;

/// <summary>
/// Maps Avalonia <see cref="KeyEventArgs"/> to the byte sequences that
/// xterm-compatible terminals send over the PTY. Covers the common
/// 95% — arrows, F1-F12, Home/End/PgUp/PgDn, Delete, Enter, Tab,
/// Backspace, Ctrl+letter, Alt+letter (as ESC-prefix), plus printable
/// characters that come in via TextInput.
///
/// <para>Anything unhandled returns an empty span so the caller can
/// decide to fall back (e.g. to TextInput events).</para>
/// </summary>
public static class KeyMapper
{
    /// <summary>
    /// Try to translate a key press into bytes. Returns the byte count;
    /// zero means "no mapping — let TextInput handle it".
    /// </summary>
    public static byte[] Map(KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        bool ctrl  = (mods & KeyModifiers.Control) != 0;
        bool alt   = (mods & KeyModifiers.Alt)     != 0;
        bool shift = (mods & KeyModifiers.Shift)   != 0;
        bool meta  = (mods & KeyModifiers.Meta)    != 0;

        // Cmd (Meta on macOS) typically isn't sent to the terminal —
        // it's consumed by the app for menus/shortcuts. Ignore those
        // here; consumers that want "Cmd as Meta" can wire it up
        // separately.
        if (meta && !ctrl && !alt) return Array.Empty<byte>();

        // Named keys — arrows, function keys, nav. These are fixed
        // xterm escape sequences.
        switch (e.Key)
        {
            case Key.Up:        return Esc("[A");
            case Key.Down:      return Esc("[B");
            case Key.Right:     return Esc("[C");
            case Key.Left:      return Esc("[D");

            case Key.Home:      return Esc("[H");
            case Key.End:       return Esc("[F");
            case Key.PageUp:    return Esc("[5~");
            case Key.PageDown:  return Esc("[6~");
            case Key.Insert:    return Esc("[2~");
            case Key.Delete:    return Esc("[3~");

            case Key.F1:   return Esc("OP");
            case Key.F2:   return Esc("OQ");
            case Key.F3:   return Esc("OR");
            case Key.F4:   return Esc("OS");
            case Key.F5:   return Esc("[15~");
            case Key.F6:   return Esc("[17~");
            case Key.F7:   return Esc("[18~");
            case Key.F8:   return Esc("[19~");
            case Key.F9:   return Esc("[20~");
            case Key.F10:  return Esc("[21~");
            case Key.F11:  return Esc("[23~");
            case Key.F12:  return Esc("[24~");

            case Key.Enter:     return new byte[] { 0x0D };       // CR (shells usually translate CR→NL)
            case Key.Tab:       return shift ? Esc("[Z") : new byte[] { 0x09 };
            case Key.Back:      return new byte[] { 0x7F };       // DEL — what most shells expect for Backspace
            case Key.Escape:    return new byte[] { 0x1B };
            case Key.Space:     return new byte[] { 0x20 };
        }

        // Ctrl+A..Z → 0x01..0x1A
        if (ctrl && !alt && e.Key >= Key.A && e.Key <= Key.Z)
        {
            byte b = (byte)(e.Key - Key.A + 1);
            return new byte[] { b };
        }

        // Common Ctrl-symbol mappings: Ctrl+@ = NUL, Ctrl+[ = ESC,
        // Ctrl+\ = FS, Ctrl+] = GS, Ctrl+^ = RS, Ctrl+_ = US.
        if (ctrl && !alt)
        {
            switch (e.Key)
            {
                case Key.D2: return new byte[] { 0x00 }; // Ctrl+@ on US layouts
                case Key.D6: return new byte[] { 0x1E }; // Ctrl+^
                case Key.OemMinus: return new byte[] { 0x1F }; // Ctrl+_
            }
        }

        return Array.Empty<byte>();
    }

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
        var bytes = Encoding.ASCII.GetBytes(tail);
        var result = new byte[bytes.Length + 1];
        result[0] = 0x1B;
        System.Buffer.BlockCopy(bytes, 0, result, 1, bytes.Length);
        return result;
    }
}
