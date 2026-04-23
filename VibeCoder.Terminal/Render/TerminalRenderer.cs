using System;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Media;
using VibeCoder.Terminal.Buffer;

namespace VibeCoder.Terminal.Render;

/// <summary>
/// Avalonia <see cref="DrawingContext"/>-based renderer. Draws the
/// visible rows of a <see cref="TerminalBuffer"/> using cached font
/// metrics. Handles wide cells, selection, hyperlinks, strikethrough,
/// bold/italic, cursor styles, and scrollback viewport via
/// <see cref="TerminalBuffer.GetRowForRender(int)"/>.
/// </summary>
public sealed class TerminalRenderer
{
    private readonly Typeface _typeface;
    private readonly double   _fontSize;

    public double CellWidth  { get; private set; }
    public double CellHeight { get; private set; }

    /// <summary>Toggled by the cursor-blink timer on
    /// <see cref="TerminalControl"/>. When <c>false</c> and the active
    /// cursor style is a "blink" variant, the cursor is hidden for one
    /// blink cycle.</summary>
    public bool BlinkVisible { get; set; } = true;

    public TerminalRenderer(
        string fontFamily = "JetBrainsMono, Menlo, monospace",
        double fontSize   = 13)
    {
        _typeface = new Typeface(fontFamily);
        _fontSize = fontSize;
        MeasureCell();
    }

    private void MeasureCell()
    {
        var ft = new FormattedText("M", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _typeface, _fontSize, Brushes.White);
        CellWidth  = ft.WidthIncludingTrailingWhitespace;
        CellHeight = ft.Height;
    }

    public (int Cols, int Rows) ComputeGrid(Size available)
    {
        if (CellWidth <= 0 || CellHeight <= 0) return (80, 24);
        return (Math.Max(1, (int)(available.Width  / CellWidth)),
                Math.Max(1, (int)(available.Height / CellHeight)));
    }

    public void Render(DrawingContext ctx, TerminalBuffer buf,
        Size size, bool focused, TerminalTheme? theme = null)
    {
        var defBg = theme?.Background ?? TerminalPalette.DefaultBackground;
        ctx.FillRectangle(new SolidColorBrush(defBg), new Rect(size));

        for (int r = 0; r < buf.Rows; r++)
        {
            var row = buf.GetRowForRender(r);
            if (row != null) DrawRow(ctx, buf, row, r, defBg, theme);
        }

        if (buf.Selection != null) DrawSelection(ctx, buf);

        DrawCursor(ctx, buf, focused, theme);
    }

    private void DrawRow(DrawingContext ctx, TerminalBuffer buf,
        TerminalCell[] row, int r, Color defBg, TerminalTheme? theme)
    {
        double y = r * CellHeight;
        int c = 0;
        while (c < row.Length)
        {
            // Right half of a wide cell — drawn by the wide cell itself.
            if ((row[c].Flags2 & CellFlags2.IsContinuation) != 0) { c++; continue; }

            var cell    = row[c];
            bool isWide = (cell.Flags2 & CellFlags2.IsWide) != 0;

            // Extend a run of same-attribute NARROW cells so we can draw
            // their glyphs with one FormattedText. Wide cells always
            // render standalone (glyph metrics aren't monospace).
            int runStart = c;
            if (!isWide)
            {
                while (c < row.Length
                    && (row[c].Flags2 & CellFlags2.IsContinuation) == 0
                    && (row[c].Flags2 & CellFlags2.IsWide)         == 0
                    && row[c].FgIndex == cell.FgIndex
                    && row[c].BgIndex == cell.BgIndex
                    && row[c].Flags   == cell.Flags
                    && row[c].FgRgb   == cell.FgRgb
                    && row[c].BgRgb   == cell.BgRgb
                    && (row[c].Rune == 0) == (cell.Rune == 0))
                {
                    c++;
                }
            }
            else
            {
                c += 2; // wide cell occupies two slots
            }

            int    runLen  = c - runStart;
            double x       = runStart * CellWidth;
            double runW    = runLen   * CellWidth;
            var    runRect = new Rect(x, y, runW, CellHeight);

            bool  inv = (cell.Flags & CellFlags.Inverse) != 0;
            Color fg  = inv ? ResolveBg(cell, defBg, theme) : ResolveFg(cell, theme);
            Color bg  = inv ? ResolveFg(cell, theme)        : ResolveBg(cell, defBg, theme);

            if (bg != defBg)
                ctx.FillRectangle(new SolidColorBrush(bg), runRect);

            if (cell.Rune != 0)
            {
                int glyphCount = isWide ? 1 : runLen;
                DrawGlyphs(ctx, row, runStart, glyphCount, x, y, fg, cell.Flags);
            }

            // OSC 8 hyperlink: subtle underline to signal clickability.
            if (cell.HyperlinkId != 0)
            {
                double ly = y + CellHeight - 1;
                ctx.DrawLine(new Pen(new SolidColorBrush(fg), 1),
                    new Point(x, ly), new Point(x + runW, ly));
            }
        }
    }

    private void DrawGlyphs(DrawingContext ctx, TerminalCell[] row,
        int start, int len, double x, double y, Color fg, CellFlags flags)
    {
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++)
        {
            int rune = row[start + i].Rune;
            if (rune == 0)           sb.Append(' ');
            else if (rune <= 0xFFFF) sb.Append((char)rune);
            else                     sb.Append(char.ConvertFromUtf32(rune));
        }

        bool bold   = (flags & CellFlags.Bold)   != 0;
        bool italic = (flags & CellFlags.Italic) != 0;
        var tf = new Typeface(_typeface.FontFamily,
            italic ? FontStyle.Italic : FontStyle.Normal,
            bold   ? FontWeight.Bold  : FontWeight.Normal);

        var ft = new FormattedText(sb.ToString(), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, tf, _fontSize, new SolidColorBrush(fg));
        ctx.DrawText(ft, new Point(x, y));

        double w = ft.WidthIncludingTrailingWhitespace;

        if ((flags & CellFlags.Underline) != 0)
        {
            double ly = y + CellHeight - 2;
            ctx.DrawLine(new Pen(new SolidColorBrush(fg), 1),
                new Point(x, ly), new Point(x + w, ly));
        }
        if ((flags & CellFlags.Strikethrough) != 0)
        {
            double ly = y + CellHeight * 0.5;
            ctx.DrawLine(new Pen(new SolidColorBrush(fg), 1),
                new Point(x, ly), new Point(x + w, ly));
        }
    }

    private void DrawSelection(DrawingContext ctx, TerminalBuffer buf)
    {
        var sel = buf.Selection!;
        var (r1, c1, r2, c2) = sel.Normalized();
        var brush = new SolidColorBrush(Color.FromArgb(0x60, 0x58, 0x9A, 0xF8));
        for (int r = Math.Max(r1, 0); r <= Math.Min(r2, buf.Rows - 1); r++)
        {
            int cs = r == r1 ? c1 : 0;
            int ce = r == r2 ? c2 : buf.Cols - 1;
            ctx.FillRectangle(brush,
                new Rect(cs * CellWidth, r * CellHeight,
                         (ce - cs + 1) * CellWidth, CellHeight));
        }
    }

    private void DrawCursor(DrawingContext ctx, TerminalBuffer buf,
        bool focused, TerminalTheme? theme)
    {
        if (!buf.CursorVisible || buf.ScrollOffset > 0) return;

        double x = buf.CursorCol * CellWidth;
        double y = buf.CursorRow * CellHeight;
        var color = theme?.Cursor ?? TerminalPalette.DefaultCursor;
        var brush = new SolidColorBrush(color);

        bool blinks = buf.CursorStyle is
            CursorStyle.BlockBlink or CursorStyle.UnderlineBlink or CursorStyle.BarBlink;
        bool invisible = blinks && !BlinkVisible;

        if (focused && !invisible)
        {
            switch (buf.CursorStyle)
            {
                case CursorStyle.BlockBlink:
                case CursorStyle.Block:
                {
                    var rect = new Rect(x, y, CellWidth, CellHeight);
                    ctx.FillRectangle(brush, rect);
                    if (buf.CursorCol < buf.Cols && buf.CursorRow < buf.Rows)
                    {
                        var cell = buf.GetVisibleRow(buf.CursorRow)[buf.CursorCol];
                        if (cell.Rune != 0)
                        {
                            var inv = new SolidColorBrush(
                                theme?.Background ?? TerminalPalette.DefaultBackground);
                            var ft = new FormattedText(
                                char.ConvertFromUtf32(cell.Rune),
                                CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                _typeface, _fontSize, inv);
                            ctx.DrawText(ft, new Point(x, y));
                        }
                    }
                    break;
                }
                case CursorStyle.UnderlineBlink:
                case CursorStyle.Underline:
                    ctx.DrawLine(new Pen(brush, 2),
                        new Point(x, y + CellHeight - 2),
                        new Point(x + CellWidth, y + CellHeight - 2));
                    break;
                case CursorStyle.BarBlink:
                case CursorStyle.Bar:
                    ctx.DrawLine(new Pen(brush, 2),
                        new Point(x, y), new Point(x, y + CellHeight));
                    break;
            }
        }
        else
        {
            ctx.DrawRectangle(null, new Pen(brush, 1),
                new Rect(x, y, CellWidth, CellHeight));
        }
    }

    private static Color ResolveFg(TerminalCell c, TerminalTheme? theme)
    {
        if ((c.Flags & CellFlags.FgRgb) != 0) return Color.FromUInt32(0xFF000000 | c.FgRgb);
        if (c.FgIndex == 0 && c.FgRgb == 0)   return theme?.Foreground ?? TerminalPalette.DefaultForeground;
        if (theme?.AnsiColors != null && c.FgIndex < 16 && theme.AnsiColors[c.FgIndex].HasValue)
            return theme.AnsiColors[c.FgIndex]!.Value;
        return TerminalPalette.FromIndex(c.FgIndex);
    }

    private static Color ResolveBg(TerminalCell c, Color defBg, TerminalTheme? theme)
    {
        if ((c.Flags & CellFlags.BgRgb) != 0) return Color.FromUInt32(0xFF000000 | c.BgRgb);
        if (c.BgIndex == 0 && c.BgRgb == 0)   return defBg;
        if (theme?.AnsiColors != null && c.BgIndex < 16 && theme.AnsiColors[c.BgIndex].HasValue)
            return theme.AnsiColors[c.BgIndex]!.Value;
        return TerminalPalette.FromIndex(c.BgIndex);
    }
}
