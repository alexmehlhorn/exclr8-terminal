namespace VibeCoder.Terminal.Buffer;

/// <summary>
/// One character cell in the terminal grid. Packed into 8 bytes.
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

    public static readonly TerminalCell Blank = default;

    public bool IsBlank => Rune == 0;
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
    /// <summary>DECDWL/DECDHL line attribute — tracked but not
    /// fully rendered (see the feature matrix note). Used so we can
    /// surface the "not implemented" state if the app layer inspects.</summary>
    DoubleWidth    = 1 << 3,
    /// <summary>DECDHL top half.</summary>
    DoubleHeightTop    = 1 << 4,
    /// <summary>DECDHL bottom half.</summary>
    DoubleHeightBottom = 1 << 5,
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
