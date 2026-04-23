using System;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using VibeCoder.Terminal.Buffer;

namespace VibeCoder.Terminal.Render;

/// <summary>
/// Avalonia <see cref="DrawingContext"/>-based renderer. Draws the
/// visible rows of a <see cref="TerminalBuffer"/> using cached
/// <see cref="Typeface"/> and measured monospace cell metrics.
///
/// <para>Avoids <see cref="FormattedText"/>-per-cell churn by drawing
/// one FormattedText PER RUN of same-attribute cells in a row. That
/// keeps throughput reasonable even before we switch to a Skia glyph-
/// cache path.</para>
/// </summary>
public sealed class TerminalRenderer
{
    private readonly Typeface _typeface;
    private readonly double _fontSize;

    public double CellWidth { get; private set; }
    public double CellHeight { get; private set; }
    public double Baseline { get; private set; }

    public TerminalRenderer(string fontFamily = "JetBrainsMono, Menlo, monospace", double fontSize = 13)
    {
        _typeface = new Typeface(fontFamily);
        _fontSize = fontSize;
        MeasureCell();
    }

    private void MeasureCell()
    {
        // Measure an 'M' (widest typical glyph in a monospace font) to
        // get cell dimensions. Baseline = distance from top of line box
        // to where glyphs sit on — used when drawing text at (x, y).
        var sample = new FormattedText(
            "M",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _typeface,
            _fontSize,
            Brushes.White);
        CellWidth = sample.WidthIncludingTrailingWhitespace;
        CellHeight = sample.Height;
        Baseline = sample.Baseline;
    }

    public (int Cols, int Rows) ComputeGrid(Size available)
    {
        if (CellWidth <= 0 || CellHeight <= 0) return (80, 24);
        int cols = Math.Max(1, (int)(available.Width / CellWidth));
        int rows = Math.Max(1, (int)(available.Height / CellHeight));
        return (cols, rows);
    }

    public void Render(DrawingContext ctx, TerminalBuffer buffer, Size size, bool focused)
    {
        // Background.
        ctx.FillRectangle(new SolidColorBrush(TerminalPalette.DefaultBackground), new Rect(size));

        // Draw each visible row.
        for (int r = 0; r < buffer.Rows; r++)
        {
            DrawRow(ctx, buffer, r);
        }

        DrawCursor(ctx, buffer, focused);
    }

    private void DrawRow(DrawingContext ctx, TerminalBuffer buffer, int rowIndex)
    {
        var row = buffer.GetVisibleRow(rowIndex);
        double y = rowIndex * CellHeight;

        // Walk the row splitting into runs of same-attribute cells so
        // we can render each run with one FormattedText call. A "blank"
        // cell (Rune==0) gets default bg — we still paint contiguous
        // background runs to keep the grid solid, but skip glyphs.
        int c = 0;
        while (c < row.Length)
        {
            var runStart = c;
            var runCell = row[c];
            var runRune = runCell.Rune;
            while (c < row.Length
                && row[c].FgIndex == runCell.FgIndex
                && row[c].BgIndex == runCell.BgIndex
                && row[c].Flags == runCell.Flags
                && row[c].FgRgb == runCell.FgRgb
                && row[c].BgRgb == runCell.BgRgb
                && (row[c].Rune == 0) == (runRune == 0))
            {
                c++;
            }
            int runLen = c - runStart;
            double x = runStart * CellWidth;
            var runRect = new Rect(x, y, runLen * CellWidth, CellHeight);

            // Background (skip if it's the default — we already painted
            // the whole control bg).
            var bg = ResolveBackground(runCell);
            if (bg != TerminalPalette.DefaultBackground)
            {
                ctx.FillRectangle(new SolidColorBrush(bg), runRect);
            }

            if (runRune != 0)
            {
                DrawRunGlyphs(ctx, row, runStart, runLen, x, y);
            }
        }
    }

    private void DrawRunGlyphs(DrawingContext ctx, TerminalCell[] row, int start, int len, double x, double y)
    {
        // Build a string for this run. Most cells are single-rune so
        // this is just a char-by-char copy; proper surrogate handling
        // is on the TODO list.
        var buf = new char[len];
        for (int i = 0; i < len; i++)
        {
            int rune = row[start + i].Rune;
            buf[i] = rune == 0 ? ' ' : (char)rune;
        }
        var sample = row[start];
        var fg = ResolveForeground(sample);
        var ft = new FormattedText(
            new string(buf),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _typeface,
            _fontSize,
            new SolidColorBrush(fg));
        ctx.DrawText(ft, new Point(x, y));
    }

    private void DrawCursor(DrawingContext ctx, TerminalBuffer buffer, bool focused)
    {
        double x = buffer.CursorCol * CellWidth;
        double y = buffer.CursorRow * CellHeight;
        var rect = new Rect(x, y, CellWidth, CellHeight);
        var brush = new SolidColorBrush(TerminalPalette.DefaultCursor);
        if (focused)
        {
            ctx.FillRectangle(brush, rect);
            // Draw the cell's glyph inverted (default bg) on top so the
            // character under the cursor stays visible.
            if (buffer.CursorCol < buffer.Cols && buffer.CursorRow < buffer.Rows)
            {
                var cell = buffer.GetVisibleRow(buffer.CursorRow)[buffer.CursorCol];
                if (cell.Rune != 0)
                {
                    var ft = new FormattedText(
                        ((char)cell.Rune).ToString(),
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        _typeface,
                        _fontSize,
                        new SolidColorBrush(TerminalPalette.DefaultBackground));
                    ctx.DrawText(ft, new Point(x, y));
                }
            }
        }
        else
        {
            // Unfocused: outline only.
            ctx.DrawRectangle(null, new Pen(brush, 1), rect);
        }
    }

    private static Color ResolveForeground(TerminalCell c)
    {
        if (c.Flags.HasFlag(CellFlags.FgRgb)) return Color.FromUInt32(0xFF000000 | c.FgRgb);
        if (c.FgIndex == 0 && c.FgRgb == 0) return TerminalPalette.DefaultForeground;
        return TerminalPalette.FromIndex(c.FgIndex);
    }

    private static Color ResolveBackground(TerminalCell c)
    {
        if (c.Flags.HasFlag(CellFlags.BgRgb)) return Color.FromUInt32(0xFF000000 | c.BgRgb);
        if (c.BgIndex == 0 && c.BgRgb == 0) return TerminalPalette.DefaultBackground;
        return TerminalPalette.FromIndex(c.BgIndex);
    }
}
