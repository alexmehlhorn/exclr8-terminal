namespace Exclr8.Terminal.Render;

/// <summary>
/// Terminal cursor shape — set via DECSCUSR (<c>CSI Ps SP q</c>).
/// Values 0/1 map to <see cref="BlockBlink"/>.
/// </summary>
public enum CursorStyle
{
    BlockBlink,
    Block,
    UnderlineBlink,
    Underline,
    BarBlink,
    Bar,
}
