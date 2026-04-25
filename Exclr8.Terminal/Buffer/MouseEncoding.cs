namespace Exclr8.Terminal.Buffer;

/// <summary>
/// Wire format the terminal uses for mouse events. Set by DECSET 1006
/// (Sgr) / 1016 (SgrPixels). The default is the legacy xterm X10
/// encoding, which can only address columns and rows up to 223 because
/// each coordinate is packed into one byte after a +32 offset.
/// </summary>
public enum MouseEncoding : byte
{
    /// <summary>Legacy xterm X10/1000 encoding: <c>ESC [ M Cb Cx Cy</c>
    /// where Cb/Cx/Cy are single bytes with +32 offset. Columns &gt; 223
    /// can't be encoded.</summary>
    Default,
    /// <summary>SGR encoding (DECSET 1006): <c>ESC [ &lt; b ; x ; y M</c>
    /// for press, <c>m</c> for release. Decimal-encoded — no column
    /// limit. The encoding modern apps prefer.</summary>
    Sgr,
    /// <summary>SGR-pixel encoding (DECSET 1016): like Sgr but x and y
    /// are pixel coordinates instead of cell. Used by editors that
    /// want sub-cell precision (e.g. for image-based UIs).</summary>
    SgrPixels,
}
