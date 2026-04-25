namespace Exclr8.Terminal.Buffer;

/// <summary>
/// SGR 4:N underline style. 0 = none (no underline drawn even if the
/// Underline flag is set — defensive). Mirrors the kitty / WezTerm
/// extension that's now widely supported.
/// </summary>
public enum UnderlineStyle : byte
{
    None    = 0,
    Single  = 1,
    Double  = 2,
    Curly   = 3,
    Dotted  = 4,
    Dashed  = 5,
}

/// <summary>
/// One character cell in the terminal grid.
///
/// <para>Colors can be either 256-palette indices (<see cref="FgIndex"/> /
/// <see cref="BgIndex"/>) or 24-bit RGB (when <see cref="CellFlags.FgRgb"/> /
/// <see cref="CellFlags.BgRgb"/> is set, the packed RGB lives in the
/// higher byte lanes).</para>
///
/// <para>Zero-initialised instances render as a blank cell on default
/// fg/bg, which lets us allocate rows via <c>new TerminalCell[N]</c>
/// without a fill loop.</para>
/// </summary>
public struct TerminalCell
{
    /// <summary>UTF-32 rune. 0 = empty cell.</summary>
    public int Rune;

    /// <summary>Foreground 256-palette index (used when <c>FgRgb</c> flag unset).</summary>
    public byte FgIndex;

    /// <summary>Background 256-palette index (used when <c>BgRgb</c> flag unset).</summary>
    public byte BgIndex;

    /// <summary>Style + rgb-or-indexed flags.</summary>
    public CellFlags Flags;

    /// <summary>Wide-char / continuation flags. Separate byte so CellFlags
    /// stays a 7-bit legacy palette + stays easy to binary-compare.</summary>
    public CellFlags2 Flags2;

    /// <summary>OSC 8 hyperlink ID (0 = no link). Maps to a URL via
    /// <see cref="TerminalBuffer.TryGetHyperlink(ushort,out string)"/>.</summary>
    public ushort HyperlinkId;

    // RGB packing when FgRgb / BgRgb are set — stored in a separate uint
    // (kept out of the main struct for size; zero == default colors).
    public uint FgRgb;
    public uint BgRgb;

    /// <summary>SGR 4:N underline style. Default 0 = no special form;
    /// when <see cref="CellFlags.Underline"/> is set we treat 0 the
    /// same as <see cref="UnderlineStyle.Single"/> for backward
    /// compatibility with plain SGR 4.</summary>
    public UnderlineStyle UnderlineStyle;

    /// <summary>SGR 58 underline colour. 0 = use the foreground.
    /// Stored as packed 0xRRGGBB; <see cref="CellFlags2.UlColorSet"/>
    /// must be true for the renderer to honour this — otherwise it
    /// uses the cell's foreground.</summary>
    public uint UnderlineRgb;

    public static readonly TerminalCell Blank = default;
}

[System.Flags]
public enum CellFlags2 : byte
{
    None           = 0,
    /// <summary>East Asian Wide / emoji — this cell occupies 2 columns.</summary>
    IsWide         = 1 << 0,
    /// <summary>Right half of a wide cell — carries no glyph of its own.</summary>
    IsContinuation = 1 << 1,
    /// <summary>SGR 5 (slow blink) / SGR 6 (rapid). The renderer toggles
    /// visibility on the shared blink timer; SGR 25 clears this flag.</summary>
    Blink          = 1 << 2,
    /// <summary>SGR 58 set an underline colour. Renderer uses
    /// <see cref="TerminalCell.UnderlineRgb"/> when this is set,
    /// otherwise falls back to the foreground.</summary>
    UlColorSet     = 1 << 3,
}

[System.Flags]
public enum CellFlags : byte
{
    None          = 0,
    Bold          = 1 << 0,
    Italic        = 1 << 1,
    Underline     = 1 << 2,
    Inverse       = 1 << 3,
    Dim           = 1 << 4,
    /// <summary>When set, <see cref="TerminalCell.FgRgb"/> is authoritative
    /// (24-bit color) instead of <see cref="TerminalCell.FgIndex"/>.</summary>
    FgRgb         = 1 << 5,
    /// <summary>Same for background.</summary>
    BgRgb         = 1 << 6,
    Strikethrough = 1 << 7,
}
