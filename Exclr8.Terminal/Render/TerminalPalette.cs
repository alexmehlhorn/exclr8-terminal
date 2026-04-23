using Avalonia.Media;

namespace Exclr8.Terminal.Render;

/// <summary>
/// 256-color terminal palette. The first 16 entries are the "ANSI"
/// colors (standard + bright), 16-231 are a 6×6×6 RGB cube, 232-255
/// are grayscale. Matches the xterm-256color palette most terminals
/// ship with.
/// </summary>
public static class TerminalPalette
{
    public static readonly Color DefaultForeground = Color.FromRgb(0xe6, 0xed, 0xf3);
    public static readonly Color DefaultBackground = Color.FromRgb(0x0d, 0x11, 0x17);
    public static readonly Color DefaultCursor     = Color.FromRgb(0xc9, 0xd1, 0xd9);

    public static readonly Color[] Indexed = BuildPalette();

    public static Color FromIndex(byte i) => Indexed[i];

    private static Color[] BuildPalette()
    {
        var p = new Color[256];

        // 0-7: standard ANSI
        p[0]  = Color.FromRgb(0x00, 0x00, 0x00); // black
        p[1]  = Color.FromRgb(0xcd, 0x00, 0x00); // red
        p[2]  = Color.FromRgb(0x00, 0xcd, 0x00); // green
        p[3]  = Color.FromRgb(0xcd, 0xcd, 0x00); // yellow
        p[4]  = Color.FromRgb(0x00, 0x00, 0xee); // blue
        p[5]  = Color.FromRgb(0xcd, 0x00, 0xcd); // magenta
        p[6]  = Color.FromRgb(0x00, 0xcd, 0xcd); // cyan
        p[7]  = Color.FromRgb(0xe5, 0xe5, 0xe5); // white

        // 8-15: bright ANSI
        p[8]  = Color.FromRgb(0x7f, 0x7f, 0x7f);
        p[9]  = Color.FromRgb(0xff, 0x00, 0x00);
        p[10] = Color.FromRgb(0x00, 0xff, 0x00);
        p[11] = Color.FromRgb(0xff, 0xff, 0x00);
        p[12] = Color.FromRgb(0x5c, 0x5c, 0xff);
        p[13] = Color.FromRgb(0xff, 0x00, 0xff);
        p[14] = Color.FromRgb(0x00, 0xff, 0xff);
        p[15] = Color.FromRgb(0xff, 0xff, 0xff);

        // 16-231: 6x6x6 RGB cube. Stops are 0, 95, 135, 175, 215, 255.
        byte[] stops = { 0, 95, 135, 175, 215, 255 };
        int idx = 16;
        for (int r = 0; r < 6; r++)
        for (int g = 0; g < 6; g++)
        for (int b = 0; b < 6; b++)
            p[idx++] = Color.FromRgb(stops[r], stops[g], stops[b]);

        // 232-255: 24 greyscale ramps (8, 18, 28, ..., 238).
        for (int i = 0; i < 24; i++)
        {
            byte v = (byte)(8 + i * 10);
            p[232 + i] = Color.FromRgb(v, v, v);
        }

        return p;
    }
}
