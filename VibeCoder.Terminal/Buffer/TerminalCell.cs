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

    // RGB packing when FgRgb / BgRgb are set — stored in a separate uint
    // (kept out of the main struct for size; zero == default colors).
    public uint FgRgb;
    public uint BgRgb;

    public static readonly TerminalCell Blank = default;

    public bool IsBlank => Rune == 0;
}

[System.Flags]
public enum CellFlags : byte
{
    None       = 0,
    Bold       = 1 << 0,
    Italic     = 1 << 1,
    Underline  = 1 << 2,
    Inverse    = 1 << 3,
    Dim        = 1 << 4,
    /// <summary>When set, <see cref="TerminalCell.FgRgb"/> is authoritative
    /// (24-bit color) instead of <see cref="TerminalCell.FgIndex"/>.</summary>
    FgRgb      = 1 << 5,
    /// <summary>Same for background.</summary>
    BgRgb      = 1 << 6,
}
