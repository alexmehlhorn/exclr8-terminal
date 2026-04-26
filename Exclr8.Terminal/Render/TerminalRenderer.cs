using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
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

    /// <summary>Whether to draw a 1-px underline beneath OSC 8
    /// hyperlink cells. See <see cref="TerminalControl.ShowHyperlinkUnderline"/>
    /// for the rationale.</summary>
    public bool ShowHyperlinkUnderline { get; set; } = true;

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
            InvalidateFontCaches();
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
            InvalidateFontCaches();
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

    /// <summary>Enable OpenType ligatures (<c>liga</c> / <c>clig</c> /
    /// <c>calt</c>) on glyph runs. Programming fonts (Fira Code,
    /// JetBrains Mono, Cascadia Code, …) substitute multi-character
    /// sequences like <c>==</c>, <c>-&gt;</c>, <c>!=</c> with composite
    /// glyphs — this turns those substitutions on. Off by default
    /// because plain monospace fonts without ligatures get nothing
    /// from it and the feature flip invalidates the layout cache.
    /// </summary>
    public bool EnableLigatures
    {
        get => _enableLigatures;
        set
        {
            if (_enableLigatures == value) return;
            _enableLigatures = value;
            // Cached layouts were built without ligatures (or with them);
            // either way they no longer match the active feature set.
            _textCache.Clear();
            _textLru.Clear();
        }
    }
    private bool _enableLigatures;

    /// <summary>OpenType feature set applied when
    /// <see cref="EnableLigatures"/> is true. Built once and reused —
    /// FontFeature has no per-frame state.</summary>
    private static readonly FontFeatureCollection LigaFeatures = new()
    {
        new FontFeature { Tag = "liga", Value = 1, Start = 0, End = int.MaxValue },
        new FontFeature { Tag = "clig", Value = 1, Start = 0, End = int.MaxValue },
        new FontFeature { Tag = "calt", Value = 1, Start = 0, End = int.MaxValue },
    };

    // ---- Allocation caches ----
    // Per-frame content changes character-by-character but the set of
    // distinct colours is small (default fg/bg, palette indices, a
    // handful of 24-bit RGB values in typical output). Rebuilding
    // SolidColorBrush + Typeface + FormattedText from scratch on every
    // run chews CPU and GC; these caches collapse repeats.
    private readonly Dictionary<uint, ImmutableSolidColorBrush> _brushCache = new();
    // Immutable pens keyed on (colour, thickness * 10). Pens wrap a
    // brush + thickness; a few thicknesses show up repeatedly (1px for
    // underline/strikethrough/hyperlink/unfocused-cursor, 2px for focused
    // cursor), so caching collapses them all.
    private readonly Dictionary<(uint Color, int Thickness10), ImmutablePen> _penCache = new();
    // Typeface variants for the current typeface: index = (bold?1:0) | (italic?2:0).
    private readonly Typeface[] _typefaceVariants = new Typeface[4];
    // FormattedText layout is expensive to build; keep a bounded
    // LRU cache keyed on a hash of (StringBuilder contents, variant,
    // size, fg, liga). Hashing the StringBuilder directly means a
    // cache hit doesn't need to materialise a key string — we verify
    // via char-by-char compare against the stored entry's text. Only
    // a cache miss pays the ToString allocation.
    //
    // LRU rather than full-clear-on-overflow. In a long Claude / IDE
    // streaming session the working set comfortably exceeds the cap
    // (200-400 distinct glyph runs per syntax-highlighted screen);
    // full-clear thrashed the hot punctuation / spaces / common
    // keywords every time the cap was hit. LRU keeps those forever
    // and only evicts genuine one-off identifiers.
    private readonly Dictionary<int, LinkedListNode<TextCacheEntry>> _textCache = new();
    private readonly LinkedList<TextCacheEntry> _textLru = new();
    private const int TextCacheMax = 512;
    // Reused across glyph runs so we don't allocate a fresh StringBuilder
    // per run. Cleared at the start of each DrawGlyphs call.
    private readonly StringBuilder _glyphSb = new();

    private sealed class TextCacheEntry
    {
        public int Hash;
        public string Text = "";
        public int Variant;
        public int SizeTenths;
        public uint Fg;
        public bool Liga;
        public FormattedText Ft = null!;
    }

    private static uint ColorKey(Color c) =>
        ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    private ImmutableSolidColorBrush BrushFor(Color c)
    {
        uint key = ColorKey(c);
        if (_brushCache.TryGetValue(key, out var brush)) return brush;
        brush = new ImmutableSolidColorBrush(c);
        _brushCache[key] = brush;
        return brush;
    }

    private ImmutablePen PenFor(Color c, double thickness)
    {
        var key = (ColorKey(c), (int)(thickness * 10));
        if (_penCache.TryGetValue(key, out var pen)) return pen;
        pen = new ImmutablePen(BrushFor(c), thickness);
        _penCache[key] = pen;
        return pen;
    }

    private Typeface TypefaceFor(bool bold, bool italic)
    {
        int idx = (bold ? 1 : 0) | (italic ? 2 : 0);
        var tf = _typefaceVariants[idx];
        if (tf.FontFamily == _typeface.FontFamily
            && tf.Weight   == (bold   ? FontWeight.Bold   : FontWeight.Normal)
            && tf.Style    == (italic ? FontStyle.Italic  : FontStyle.Normal))
        {
            return tf;
        }
        tf = new Typeface(_typeface.FontFamily,
            italic ? FontStyle.Italic : FontStyle.Normal,
            bold   ? FontWeight.Bold  : FontWeight.Normal);
        _typefaceVariants[idx] = tf;
        return tf;
    }

    /// <summary>Glyph-run cache lookup keyed directly on a StringBuilder
    /// + style. On a cache hit we compare the StringBuilder against the
    /// stored entry's text char-by-char — no string allocation needed.
    /// Only a cache miss allocates via <c>sb.ToString()</c>. Used by
    /// <see cref="DrawGlyphs"/>, which is the dominant per-frame call
    /// site; <see cref="DrawCursor"/> has its own small one-off path
    /// since it renders at most one FormattedText per frame.</summary>
    private FormattedText FormattedTextForRun(StringBuilder sb, Typeface tf, double size, Color fg)
    {
        int variantIdx = 0;
        for (int i = 0; i < _typefaceVariants.Length; i++)
        {
            if (_typefaceVariants[i].Equals(tf)) { variantIdx = i; break; }
        }
        int size10 = (int)(size * 10);
        uint fgKey = ColorKey(fg);
        bool liga = _enableLigatures;
        int hash = ComputeRunHash(sb, variantIdx, size10, fgKey, liga);

        if (_textCache.TryGetValue(hash, out var node))
        {
            var hit = node.Value;
            if (hit.Variant == variantIdx
                && hit.SizeTenths == size10
                && hit.Fg == fgKey
                && hit.Liga == liga
                && SbEqualsString(sb, hit.Text))
            {
                // Move to front of LRU. Cheap LinkedList pointer swap;
                // no allocation. Hot tokens (punctuation, single
                // spaces, common keywords) drift toward the head and
                // never evict regardless of cap pressure.
                if (node.Previous != null)
                {
                    _textLru.Remove(node);
                    _textLru.AddFirst(node);
                }
                return hit.Ft;
            }
            // Hash collision with a stale entry — drop it and miss.
            _textLru.Remove(node);
            _textCache.Remove(hash);
        }

        // Miss. Build the FormattedText, evict the LRU tail if at cap,
        // then add to the head.
        string text = sb.ToString();
        var ft = new FormattedText(text, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, tf, size, BrushFor(fg));
        // Programming-font ligatures: liga / clig / calt. Off by
        // default — ordinary monospace fonts don't substitute, and
        // turning the features on adds a per-cache-miss shaping pass.
        // Hosts that ship Fira Code / JetBrains Mono / Cascadia opt
        // in via TerminalControl.EnableLigatures.
        if (liga) ft.SetFontFeatures(LigaFeatures);

        if (_textLru.Count >= TextCacheMax)
        {
            // Evict the genuine LRU instead of nuking the whole cache —
            // see the field comment above for the streaming-session
            // rationale.
            var lruNode = _textLru.Last!;
            _textCache.Remove(lruNode.Value.Hash);
            _textLru.RemoveLast();
        }

        var entry = new TextCacheEntry
        {
            Hash = hash, Text = text, Variant = variantIdx,
            SizeTenths = size10, Fg = fgKey, Liga = liga, Ft = ft,
        };
        var newNode = _textLru.AddFirst(entry);
        _textCache[hash] = newNode;
        return ft;
    }

    private static int ComputeRunHash(StringBuilder sb, int variant, int size10, uint fg, bool liga)
    {
        var hc = new HashCode();
        hc.Add(variant);
        hc.Add(size10);
        hc.Add(fg);
        hc.Add(liga);
        int len = sb.Length;
        for (int i = 0; i < len; i++) hc.Add(sb[i]);
        return hc.ToHashCode();
    }

    private static bool SbEqualsString(StringBuilder sb, string s)
    {
        if (sb.Length != s.Length) return false;
        for (int i = 0; i < sb.Length; i++)
            if (sb[i] != s[i]) return false;
        return true;
    }

    private void InvalidateFontCaches()
    {
        for (int i = 0; i < _typefaceVariants.Length; i++) _typefaceVariants[i] = default;
        _textCache.Clear();
        _textLru.Clear();
    }

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
            InvalidateFontCaches();
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
        Size size, bool focused, TerminalTheme? theme = null,
        IReadOnlyList<ILinkProvider>? linkProviders = null)
    {
        // Default colours: shell-issued OSC 10/11/12 wins over host
        // theme, theme over static palette. Without the explicit-set
        // gate the renderer would always pick the buffer's pre-seeded
        // value and the host theme would never apply.
        Color defFg = buf.DefaultForegroundExplicit
            ? Color.FromUInt32(0xFF000000 | buf.DefaultForegroundRgb)
            : theme?.Foreground ?? TerminalPalette.DefaultForeground;
        Color defBg = buf.DefaultBackgroundExplicit
            ? Color.FromUInt32(0xFF000000 | buf.DefaultBackgroundRgb)
            : theme?.Background ?? TerminalPalette.DefaultBackground;
        ctx.FillRectangle(BrushFor(defBg), new Rect(size));

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

        // Bottom-layer decorations paint behind cell content so
        // backgrounds bleed through where the cell bg is the default.
        if (buf.Decorations.Count > 0) DrawDecorations(ctx, buf, dy, DecorationLayer.Bottom);

        for (int r = startRow; r <= endRow; r++)
        {
            var row = buf.GetRowForRender(r);
            if (row != null) DrawRow(ctx, buf, row, r, dy, defFg, defBg, theme);
        }

        // Plain-URL / custom-link underlines. Drawn after cell content
        // so the underline sits on top of the run; before the cursor /
        // selection so those still take visual priority.
        if (linkProviders != null && linkProviders.Count > 0)
            DrawLinkUnderlines(ctx, buf, linkProviders, dy);

        if (buf.Decorations.Count > 0) DrawDecorations(ctx, buf, dy, DecorationLayer.Top);

        if (buf.SearchMatches.Count > 0) DrawSearchMatches(ctx, buf, dy);
        if (buf.Selection != null)       DrawSelection(ctx, buf, dy);

        DrawCursor(ctx, buf, dy, focused, theme);
        DrawScrollbar(ctx, buf, size);
    }

    /// <summary>Hard cap on link spans we'll draw per provider per
    /// row. A misbehaving regex returning thousands of matches
    /// shouldn't tank the frame; the cap sets a predictable upper
    /// bound on render time.</summary>
    private const int MaxLinksPerProviderPerRow = 64;

    // Per-renderer scratch buffers reused across DrawLinkUnderlines
    // calls. Prevents allocating a fresh StringBuilder + int[] per
    // visible row per frame — the hottest non-trivial allocation in
    // the link-provider path.
    private readonly StringBuilder _linkRowSb = new();
    private int[] _linkColMap = new int[256];

    private void DrawLinkUnderlines(DrawingContext ctx, TerminalBuffer buf,
        IReadOnlyList<ILinkProvider> providers, double pixelShift)
    {
        // Per visible row: build text + colMap once, query each
        // provider, translate string-index coords to cell columns,
        // draw a thin underline. Astral runes occupy 2 string indices
        // but 1 cell — without colMap we'd draw the underline shifted
        // right of where the cells actually are.
        var pen = PenFor(Color.FromArgb(0xC0, 0x58, 0x9A, 0xF8), 1);
        for (int visualRow = 0; visualRow < buf.Rows; visualRow++)
        {
            var cells = buf.GetRowForRender(visualRow);
            if (cells == null) continue;
            // Two cheap rejects before we materialise text. Blank rows
            // can't have URLs; rows with no ":/" sequence can't either.
            // Both checks are O(cols); skip the StringBuilder.ToString
            // and the regex when neither could match.
            if (IsBlankCellRow(cells)) continue;
            if (!RowText.MightContainUrl(cells)) continue;

            int needMap = cells.Length * 2;
            if (_linkColMap.Length < needMap) _linkColMap = new int[needMap];
            int textLen = RowText.BuildInto(cells, _linkRowSb, _linkColMap);
            // ToString is unavoidable — providers consume `string`. But
            // we only run it for rows that actually look URL-bearing.
            string rowText = _linkRowSb.ToString();

            double y = visualRow * CellHeight + pixelShift + CellHeight - 1;
            for (int i = 0; i < providers.Count; i++)
            {
                int drawn = 0;
                foreach (var link in providers[i].Provide(rowText))
                {
                    if (drawn >= MaxLinksPerProviderPerRow) break;
                    int startCell = _linkColMap[link.StartCol];
                    int endCell   = _linkColMap[Math.Min(link.EndCol - 1, textLen - 1)];
                    double x0 = startCell * CellWidth;
                    double x1 = (endCell + 1) * CellWidth;
                    ctx.DrawLine(pen, new Point(x0, y), new Point(x1, y));
                    drawn++;
                }
            }
        }
    }

    private static bool IsBlankCellRow(TerminalCell[] cells)
    {
        for (int i = 0; i < cells.Length; i++)
            if (cells[i].Rune != 0) return false;
        return true;
    }

    private void DrawDecorations(DrawingContext ctx, TerminalBuffer buf, double pixelShift, DecorationLayer layer)
    {
        int sbCount    = buf.ScrollbackCount;
        int viewTopAbs = sbCount - buf.ScrollOffset;
        int viewBotAbs = viewTopAbs + buf.Rows - 1;
        for (int i = 0; i < buf.Decorations.Count; i++)
        {
            var d = buf.Decorations[i];
            if (d.Layer != layer || !d.Marker.IsValid) continue;
            int absRow = d.Marker.Line;
            if (absRow < viewTopAbs - 1 || absRow > viewBotAbs) continue;
            int visualRow = absRow - viewTopAbs;
            int x0 = Math.Max(0, d.X);
            int width = d.Width <= 0 ? buf.Cols - x0 : Math.Min(d.Width, buf.Cols - x0);
            if (width <= 0) continue;
            if (d.BackgroundRgb is uint bg)
            {
                ctx.FillRectangle(BrushFor(Color.FromUInt32(0xFF000000 | bg)),
                    new Rect(x0 * CellWidth,
                             visualRow * CellHeight + pixelShift,
                             width * CellWidth,
                             CellHeight));
            }
        }
    }

    /// <summary>
    /// Paint a highlight behind every search match that falls inside
    /// the visible viewport. The "current" match uses a brighter,
    /// saturated fill so it's obvious which one Enter will navigate
    /// from — every other match gets a softer wash.
    /// </summary>
    private void DrawSearchMatches(DrawingContext ctx, TerminalBuffer buf, double pixelShift)
    {
        var softBrush = BrushFor(Color.FromArgb(0x66, 0xE5, 0xC0, 0x7B));
        var liveBrush = BrushFor(Color.FromArgb(0xCC, 0xFF, 0xAA, 0x00));

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
        ctx.FillRectangle(BrushFor(Color.FromArgb(trackA, 0x8a, 0x92, 0x9c)),
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
        ctx.FillRectangle(BrushFor(Color.FromArgb(thumbA, 0xc9, 0xd1, 0xd9)),
            new Rect(x + 1, thumbY, width - 2, thumbHeight));
    }

    private void DrawRow(DrawingContext ctx, TerminalBuffer buf,
        TerminalCell[] row, int r, double pixelShift, Color defFg, Color defBg, TerminalTheme? theme)
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
                    && row[c].UnderlineStyle == cell.UnderlineStyle
                    && row[c].UnderlineRgb   == cell.UnderlineRgb
                    && (row[c].Flags2 & CellFlags2.UlColorSet)
                       == (cell.Flags2 & CellFlags2.UlColorSet)
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
            Color fg  = inv ? ResolveBg(cell, buf, defBg, theme) : ResolveFg(cell, buf, defFg, theme);
            Color bg  = inv ? ResolveFg(cell, buf, defFg, theme) : ResolveBg(cell, buf, defBg, theme);

            if (bg != defBg)
                ctx.FillRectangle(BrushFor(bg), runRect);

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
            // Toggleable so hosts that style their own button-shaped
            // links (filled bg + contrasting fg, distinct from
            // surrounding text) can opt out — see
            // TerminalControl.ShowHyperlinkUnderline.
            if (cell.HyperlinkId != 0 && ShowHyperlinkUnderline)
            {
                double ly = y + CellHeight - 1;
                ctx.DrawLine(PenFor(fg, 1),
                    new Point(x, ly), new Point(x + runW, ly));
            }
        }
    }

    private void DrawGlyphs(DrawingContext ctx, TerminalCell[] row,
        int start, int len, double x, double y, Color fg, CellFlags flags)
    {
        _glyphSb.Clear();
        _glyphSb.EnsureCapacity(len);
        for (int i = 0; i < len; i++)
        {
            int rune = row[start + i].Rune;
            if (rune == 0)           _glyphSb.Append(' ');
            else if (rune <= 0xFFFF) _glyphSb.Append((char)rune);
            else                     _glyphSb.Append(char.ConvertFromUtf32(rune));
        }

        bool bold   = (flags & CellFlags.Bold)   != 0;
        bool italic = (flags & CellFlags.Italic) != 0;
        var tf = TypefaceFor(bold, italic);

        var ft = FormattedTextForRun(_glyphSb, tf, _fontSize, fg);
        ctx.DrawText(ft, new Point(x, y));

        double w = ft.WidthIncludingTrailingWhitespace;

        if ((flags & CellFlags.Underline) != 0)
        {
            // Underline colour: SGR 58 takes precedence, else fg.
            var head = row[start];
            Color ulColor = (head.Flags2 & CellFlags2.UlColorSet) != 0
                ? Color.FromUInt32(0xFF000000 | head.UnderlineRgb)
                : fg;
            DrawUnderline(ctx, x, y, w, ulColor, head.UnderlineStyle);
        }
        if ((flags & CellFlags.Strikethrough) != 0)
        {
            double ly = y + CellHeight * 0.5;
            ctx.DrawLine(PenFor(fg, 1),
                new Point(x, ly), new Point(x + w, ly));
        }
    }

    /// <summary>Draw the SGR 4 underline style for a glyph run. Single
    /// is one straight line at baseline−2; double is two parallel; curly
    /// is a sine wave; dotted/dashed are short segments along the same
    /// baseline. Mirrors what xterm and kitty draw for these styles.</summary>
    private void DrawUnderline(DrawingContext ctx, double x, double y, double w,
        Color color, UnderlineStyle style)
    {
        double baseline = y + CellHeight - 2;
        switch (style)
        {
            case UnderlineStyle.None:
            case UnderlineStyle.Single:
                ctx.DrawLine(PenFor(color, 1),
                    new Point(x, baseline), new Point(x + w, baseline));
                break;
            case UnderlineStyle.Double:
                ctx.DrawLine(PenFor(color, 1),
                    new Point(x, baseline - 1), new Point(x + w, baseline - 1));
                ctx.DrawLine(PenFor(color, 1),
                    new Point(x, baseline + 1), new Point(x + w, baseline + 1));
                break;
            case UnderlineStyle.Curly:
            {
                // Build a sine-style stroke by chaining short segments.
                // 4 px wavelength, 1 px amplitude — close enough to
                // what kitty / iTerm draw at typical terminal sizes.
                var pen = PenFor(color, 1);
                double step = 2;
                double amp  = 1;
                Point prev = new Point(x, baseline);
                for (double dx = 0; dx <= w; dx += step)
                {
                    double phase = (dx / step) % 2;
                    Point next = new Point(x + dx, baseline + (phase < 1 ? -amp : +amp));
                    ctx.DrawLine(pen, prev, next);
                    prev = next;
                }
                break;
            }
            case UnderlineStyle.Dotted:
            {
                var pen = PenFor(color, 1);
                for (double dx = 0; dx + 1 < w; dx += 2)
                    ctx.DrawLine(pen, new Point(x + dx, baseline),
                        new Point(x + dx + 1, baseline));
                break;
            }
            case UnderlineStyle.Dashed:
            {
                var pen = PenFor(color, 1);
                for (double dx = 0; dx + 2 < w; dx += 4)
                    ctx.DrawLine(pen, new Point(x + dx, baseline),
                        new Point(x + dx + 3, baseline));
                break;
            }
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

        var brush = BrushFor(Color.FromArgb(0x60, 0x58, 0x9A, 0xF8));
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
        // OSC 12 cursor colour wins over the host theme.
        var color = buf.DefaultCursorExplicit
            ? Color.FromUInt32(0xFF000000 | buf.DefaultCursorRgb)
            : theme?.Cursor ?? TerminalPalette.DefaultCursor;
        var brush = BrushFor(color);

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
                            // Cursor inversion fires at most once per
                            // frame, and the rune changes every cursor
                            // move — caching isn't worth the churn.
                            // Allocate directly.
                            var bgColor = theme?.Background ?? TerminalPalette.DefaultBackground;
                            var ft = new FormattedText(
                                char.ConvertFromUtf32(cell.Rune),
                                CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                _typeface, _fontSize, BrushFor(bgColor));
                            ctx.DrawText(ft, new Point(x, y));
                        }
                    }
                    break;
                }
                case CursorStyle.UnderlineBlink:
                case CursorStyle.Underline:
                    ctx.DrawLine(PenFor(color, 2),
                        new Point(x, y + CellHeight - 2),
                        new Point(x + CellWidth, y + CellHeight - 2));
                    break;
                case CursorStyle.BarBlink:
                case CursorStyle.Bar:
                    ctx.DrawLine(PenFor(color, 2),
                        new Point(x, y), new Point(x, y + CellHeight));
                    break;
            }
        }
        else
        {
            ctx.DrawRectangle(null, PenFor(color, 1),
                new Rect(x, y, CellWidth, CellHeight));
        }
    }

    private static Color ResolveFg(TerminalCell c, TerminalBuffer buf, Color defFg, TerminalTheme? theme)
    {
        if ((c.Flags & CellFlags.FgRgb) != 0) return Color.FromUInt32(0xFF000000 | c.FgRgb);
        if (c.FgIndex == 0 && c.FgRgb == 0)   return defFg;
        // OSC 4 dynamic palette overrides win over the theme for that
        // specific palette slot — the shell explicitly retargeted the
        // colour, that beats whatever the host configured at startup.
        if (buf.TryGetDynamicPaletteColor(c.FgIndex, out uint dyn))
            return Color.FromUInt32(0xFF000000 | dyn);
        if (theme?.AnsiColors != null
            && c.FgIndex < 16
            && c.FgIndex < theme.AnsiColors.Length
            && theme.AnsiColors[c.FgIndex].HasValue)
            return theme.AnsiColors[c.FgIndex]!.Value;
        return TerminalPalette.FromIndex(c.FgIndex);
    }

    private static Color ResolveBg(TerminalCell c, TerminalBuffer buf, Color defBg, TerminalTheme? theme)
    {
        if ((c.Flags & CellFlags.BgRgb) != 0) return Color.FromUInt32(0xFF000000 | c.BgRgb);
        if (c.BgIndex == 0 && c.BgRgb == 0)   return defBg;
        if (buf.TryGetDynamicPaletteColor(c.BgIndex, out uint dyn))
            return Color.FromUInt32(0xFF000000 | dyn);
        if (theme?.AnsiColors != null
            && c.BgIndex < 16
            && c.BgIndex < theme.AnsiColors.Length
            && theme.AnsiColors[c.BgIndex].HasValue)
            return theme.AnsiColors[c.BgIndex]!.Value;
        return TerminalPalette.FromIndex(c.BgIndex);
    }
}
