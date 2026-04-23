namespace Exclr8.Terminal.Buffer;

/// <summary>
/// DEC Special Graphics charset (ESC ( 0). Maps ASCII 0x60-0x7E to
/// Unicode box-drawing equivalents. Used by htop, tmux, ncurses borders.
/// Table matches xterm.js <c>src/common/data/Charsets.ts</c> entry "0".
/// </summary>
internal static class DecGraphics
{
    private static readonly int[] _map = new int['~' - '`' + 1];

    static DecGraphics()
    {
        void Set(char c, int cp) => _map[c - '`'] = cp;
        Set('`', 0x25C6); Set('a', 0x2592); Set('b', 0x2409); Set('c', 0x240C);
        Set('d', 0x240D); Set('e', 0x240A); Set('f', 0x00B0); Set('g', 0x00B1);
        Set('h', 0x2424); Set('i', 0x240B); Set('j', 0x2518); Set('k', 0x2510);
        Set('l', 0x250C); Set('m', 0x2514); Set('n', 0x253C); Set('o', 0x23BA);
        Set('p', 0x23BB); Set('q', 0x2500); Set('r', 0x23BC); Set('s', 0x23BD);
        Set('t', 0x251C); Set('u', 0x2524); Set('v', 0x2534); Set('w', 0x252C);
        Set('x', 0x2502); Set('y', 0x2264); Set('z', 0x2265); Set('{', 0x03C0);
        Set('|', 0x2260); Set('}', 0x00A3); Set('~', 0x00B7);
    }

    public static int Translate(int rune)
    {
        if (rune < '`' || rune > '~') return rune;
        int mapped = _map[rune - '`'];
        return mapped == 0 ? rune : mapped;
    }
}
