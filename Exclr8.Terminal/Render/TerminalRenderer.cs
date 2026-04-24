using System;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Exclr8.Terminal.Buffer;

namespace Exclr8.Terminal.Render;

/// <summary>
/// Avalonia <see cref="DrawingContext"/>-based renderer. Draws the
/// visible rows of a <see cref="TerminalBuffer"/> using cached font
/// metrics. Handles wide cells, selection, hyperlinks, strikethrough,
/// bold/italic, cursor styles, and scrollback viewport via
/// <see cref="TerminalBuffer.GetRowForRender(int)"/>.
/// </summary>
public sealed class TerminalRenderer
{
    private Typeface _typeface;
    private string   _fontFamily;
    private double   _fontSize;

    public double CellWidth  { get; private set; }
    public double CellHeight { get; private set; }

    /// <summary>Current font size (pt). Mutable so Cmd+= / Cmd+- /
    /// Cmd+0 can zoom without tearing down the renderer. Changing it
    /// re-measures the cell; callers should trigger a grid reflow.</summary>
    public double FontSize
    {
        get => _fontSize;
        set
        {
            var v = Math.Clamp(value, 6.0, 72.0);
            if (Math.Abs(v - _fontSize) < 0.01) return;
            _fontSize = v;
            MeasureCell();
        }
    }

    /// <summary>Current font family string — fed straight into
    /// Avalonia's <see cref="Typeface"/> constructor, so accepts both
    /// simple family names and the fonts:Asset#Name, fallback list
    /// syntax. Setting triggers a typeface rebuild + cell re-measure;
    /// callers should trigger a grid reflow.</summary>
    public string FontFamily
    {
        get => _fontFamily;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? DefaultFontFamily : value;
            if (v == _fontFamily) return;
            _fontFamily = v;
            _typeface   = new Typeface(_fontFamily);
            MeasureCell();
        }
    }

    /// <summary>Default font size captured at construction — used by
    /// Cmd+0 to reset zoom.</summary>
    public double DefaultFontSize { get; }

    /// <summary>Default font family captured at construction — used
    /// as the fallback when a caller clears <see cref="FontFamily"/>.</summary>
    public string DefaultFontFamily { get; }

    /// <summary>Visual width of the scrollbar strip on the right edge.</summary>
    public const double ScrollbarWidth = 6;

    /// <summary>Width of the pointer hit zone on the right edge — a
    /// little wider than the visible bar so the user can grab it
    /// comfortably even when it's been auto-hidden.</summary>
    public const double ScrollbarHitZone = 14;

    /// <summary>0..1 multiplier applied to the scrollbar fill alphas.
    /// <see cref="TerminalControl"/> drives this to fade the bar in
    /// when the user is scrolling or hovering the hit zone, and out
    /// again after a short idle period.</summary>
    public double ScrollbarOpacity { get; set; } = 0.0;

    /// <summary>Map a vertical pointer Y (in control-local coords) to a
    /// scroll offset, given the current buffer state and the control
    /// size. Returns 0 when there is no scrollback.</summary>
    public static int YToScrollOffset(double y, int scrollbackCount, int rows, double height)
    {
        if (scrollbackCount <= 0 || height <= 0) return 0;
        double total = rows + scrollbackCount;
        double thumbRatio = rows / total;
        double thumbHeight = Math.Max(24, height * thumbRatio);
        double travel = Math.Max(1, height - thumbHeight);
        // Center the grab so the thumb tracks the cursor.
        double topInverted = Math.Clamp((y - thumbHeight / 2) / travel, 0.0, 1.0);
        // topInverted=0 → top of scrollback (offset=sb); topInverted=1 → bottom (offset=0).
        return (int)Math.Round(scrollbackCount * (1.0 - topInverted));
    }

    /// <summary>Toggled by the cursor-blink timer on
    /// <see cref="TerminalControl"/>. When <c>false</c> and the active
    /// cursor style is a "blink" variant, the cursor is hidden for one
    /// blink cycle.</summary>
    public bool BlinkVisible { get; set; } = true;

    public TerminalRenderer(
        string fontFamily = "JetBrainsMono, Menlo, monospace",
        double fontSize   = 13)
    {
        _fontFamily       = fontFamily;
        _typeface         = new Typeface(fontFamily);
        _fontSize         = fontSize;
        DefaultFontFamily = fontFamily;
        DefaultFontSize   = fontSize;
        MeasureCell();
    }

    /// <summary>True when the configured <see cref="FontFamily"/>
    /// failed a monospace-width probe and <see cref="MeasureCell"/>
    /// substituted an OS-default monospace fallback. Host code can
    /// surface this to the user if it wants to flag that the
    /// configured family isn't actually monospace.</summary>
    public bool UsingMonospaceFallback { get; private set; }

    /// <summary>Family actually in use for rendering — may differ from
    /// <see cref="FontFamily"/> if a proportional family was
    /// substituted for a platform monospace fallback.</summary>
    public string EffectiveFontFamily { get; private set; } = "";

    private void MeasureCell()
    {
        (CellWidth, CellHeight) = Measure(_typeface);
        UsingMonospaceFallback  = false;
        EffectiveFontFamily     = _fontFamily;

        // Verify the configured font is actually monospace. Compare
        // the width of a wide glyph (M) against a narrow one (i);
        // real monospace fonts draw them at the same advance, while
        // proportional families (Inter, Segoe UI, the Avalonia
        // default when a fonts: URI doesn't resolve) differ by a lot.
        // Tolerance picked empirically — 10% catches every common
        // proportional family without false-positives on legitimate
        // monospace faces like JetBrains Mono where metrics round.
        var iFt = new FormattedText("i", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _typeface, _fontSize, Brushes.White);
        var iWidth = iFt.WidthIncludingTrailingWhitespace;
        if (CellWidth > 0 && Math.Abs(CellWidth - iWidth) / CellWidth > 0.10)
        {
            // Not monospace — fall back to a platform-specific
            // monospace family. Do not recurse through FontFamily
            // setter (would loop); rebuild the typeface directly.
            var fallback = PlatformMonospaceFamily();
            _typeface              = new Typeface(fallback);
            (CellWidth, CellHeight) = Measure(_typeface);
            UsingMonospaceFallback  = true;
            EffectiveFontFamily     = fallback;
        }
    }

    private (double w, double h) Measure(Typeface tf)
    {
        var ft = new FormattedText("M", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, tf, _fontSize, Brushes.White);
        return (ft.WidthIncludingTrailingWhitespace, ft.Height);
    }

    /// <summary>Platform default monospace family — always available
    /// on the target OS without needing to ship the font ourselves.
    /// Used when the caller-supplied family turns out to be
    /// proportional.</summary>
    private static string PlatformMonospaceFamily()
    {
        if (OperatingSystem.IsMacOS())   return "Menlo";
        if (OperatingSystem.IsWindows()) return "Consolas";
        // Linux + anything else: these three cover almost every
        // distro. The first one actually installed wins via the
        // Avalonia fallback chain.
        return "DejaVu Sans Mono, Liberation Mono, monospace";
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

        // Smooth scroll: when PixelScrollOffset > 0 we're partway
        // between two rows. The whole display shifts DOWN by that
        // many pixels so an older row can bleed in at the top.
        // We render visualRow -1 (one older row, partially above
        // the viewport at the top edge) through visualRow Rows-1
        // (partly clipped at the bottom by P pixels). ClipToBounds
        // on the control hides the overflow.
        double dy = buf.PixelScrollOffset;
        int startRow = dy > 0 ? -1 : 0;
        int endRow   = buf.Rows - 1;
        for (int r = startRow; r <= endRow; r++)
        {
            var row = buf.GetRowForRender(r);
            if (row != null) DrawRow(ctx, buf, row, r, dy, defBg, theme);
        }

        if (buf.SearchMatches.Count > 0) DrawSearchMatches(ctx, buf, dy);
        if (buf.Selection != null)       DrawSelection(ctx, buf, dy);

        DrawCursor(ctx, buf, dy, focused, theme);
        DrawScrollbar(ctx, buf, size);
    }

    /// <summary>
    /// Paint a highlight behind every search match that falls inside
    /// the visible viewport. The "current" match uses a brighter,
    /// saturated fill so it's obvious which one Enter will navigate
    /// from — every other match gets a softer wash.
    /// </summary>
    private void DrawSearchMatches(DrawingContext ctx, TerminalBuffer buf, double pixelShift)
    {
        var softBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xE5, 0xC0, 0x7B));
        var liveBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xAA, 0x00));

        int sbCount     = buf.ScrollbackCount;
        int viewTopAbs  = sbCount - buf.ScrollOffset;     // visual row 0 maps to this absolute row
        int viewBotAbs  = viewTopAbs + buf.Rows - 1;
        int fromAbs     = viewTopAbs - 1;                 // -1 for sub-line scroll bleed
        int toAbs       = viewBotAbs;

        for (int i = 0; i < buf.SearchMatches.Count; i++)
        {
            var m = buf.SearchMatches[i];
            if (m.Row < fromAbs || m.Row > toAbs) continue;
            int visualRow = m.Row - viewTopAbs;
            var brush = i == buf.CurrentMatchIndex ? liveBrush : softBrush;
            ctx.FillRectangle(brush,
                new Rect(m.Col * CellWidth,
                         visualRow * CellHeight + pixelShift,
                         m.Length * CellWidth,
                         CellHeight));
        }
    }

    /// <summary>
    /// Thin scrollbar on the right edge. Only drawn when there's
    /// scrollback to represent. Proportional thumb size (viewport /
    /// total), position driven by ScrollOffset. No interaction paint —
    /// hit-testing and drag live on TerminalControl.
    /// </summary>
    private void DrawScrollbar(DrawingContext ctx, TerminalBuffer buf, Size size)
    {
        int sb = buf.ScrollbackCount;
        if (sb <= 0) return;
        double opacity = Math.Clamp(ScrollbarOpacity, 0.0, 1.0);
        if (opacity <= 0.001) return;      // fully hidden — skip draw entirely

        double width = ScrollbarWidth;
        double x = size.Width - width;
        double h = size.Height;

        // Track (faint). We're using a muted tone so it doesn't fight
        // the terminal's usual content. Alpha is multiplied by
        // ScrollbarOpacity so the whole bar fades together.
        byte trackA = (byte)(0x28 * opacity);
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(trackA, 0x8a, 0x92, 0x9c)),
            new Rect(x, 0, width, h));

        // Thumb. Total "virtual rows" = buf.Rows (visible) + sb
        // (scrollback). At ScrollOffset=0 we're showing the bottom
        // Rows rows → thumb flush to the bottom. At ScrollOffset=sb
        // we're showing the top Rows of the scrollback → thumb at top.
        // Smooth scroll: include the sub-line pixel offset so the thumb
        // tracks smoothly while the user drags a trackpad.
        double total = buf.Rows + sb;
        double thumbRatio = buf.Rows / total;
        double thumbHeight = Math.Max(24, h * thumbRatio);
        // Pixel offset is in pixels inside a line; convert to a line
        // fraction by dividing by the cell height.
        double scrolledLines = buf.ScrollOffset + (CellHeight > 0 ? buf.PixelScrollOffset / CellHeight : 0);
        double topInverted = (sb - scrolledLines) / sb;
        topInverted = Math.Clamp(topInverted, 0.0, 1.0);
        double thumbY = topInverted * (h - thumbHeight);

        byte thumbA = (byte)(0xb0 * opacity);
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(thumbA, 0xc9, 0xd1, 0xd9)),
            new Rect(x + 1, thumbY, width - 2, thumbHeight));
    }

    private void DrawRow(DrawingContext ctx, TerminalBuffer buf,
        TerminalCell[] row, int r, double pixelShift, Color defBg, TerminalTheme? theme)
    {
        double y = r * CellHeight + pixelShift;
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

            // Blinking text: when the cell carries the Blink flag and
            // the shared blink timer has flipped to "off", drop the
            // glyph entirely (but keep the background). Matches what
            // xterm does for SGR 5/6 + SGR 25 toggle.
            bool blinkHidden = (cell.Flags2 & CellFlags2.Blink) != 0 && !BlinkVisible;

            if (cell.Rune != 0 && !blinkHidden)
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

    private void DrawSelection(DrawingContext ctx, TerminalBuffer buf, double pixelShift)
    {
        var sel = buf.Selection!;
        // Selection rows are in absolute coords. Map each into the
        // current viewport and skip rows that fall outside it so
        // scrolling past the selection just hides it cleanly.
        var (r1Abs, c1, r2Abs, c2) = sel.Normalized();
        int sbCount    = buf.ScrollbackCount;
        int viewTopAbs = sbCount - buf.ScrollOffset;
        int viewBotAbs = viewTopAbs + buf.Rows - 1;

        int fromAbs = Math.Max(r1Abs, viewTopAbs - 1); // -1 for sub-line bleed
        int toAbs   = Math.Min(r2Abs, viewBotAbs);
        if (fromAbs > toAbs) return;

        var brush = new SolidColorBrush(Color.FromArgb(0x60, 0x58, 0x9A, 0xF8));
        for (int rAbs = fromAbs; rAbs <= toAbs; rAbs++)
        {
            int visualRow = rAbs - viewTopAbs;
            int cs = rAbs == r1Abs ? c1 : 0;
            int ce = rAbs == r2Abs ? c2 : buf.Cols - 1;
            ctx.FillRectangle(brush,
                new Rect(cs * CellWidth,
                         visualRow * CellHeight + pixelShift,
                         (ce - cs + 1) * CellWidth,
                         CellHeight));
        }
    }

    private void DrawCursor(DrawingContext ctx, TerminalBuffer buf,
        double pixelShift, bool focused, TerminalTheme? theme)
    {
        // Hide when viewing scrollback: ScrollOffset>0 OR mid-scroll
        // (PixelScrollOffset>0) both count as "not at the live prompt".
        if (!buf.CursorVisible || buf.ScrollOffset > 0 || pixelShift > 0) return;

        double x = buf.CursorCol * CellWidth;
        double y = buf.CursorRow * CellHeight + pixelShift;
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
