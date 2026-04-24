using System.Text;
using Avalonia.Input;
using Exclr8.Terminal.Input;
using Xunit;

namespace Exclr8.Terminal.Tests;

/// <summary>
/// Narrow tests for <see cref="KeyMapper"/>'s pure logical form —
/// enough coverage to pin down the fixes and the modifier-encoding
/// shape, not a full key-by-key exhaustion.
/// </summary>
public class KeyMapperTests
{
    private static byte[] Map(Key key, KeyModifiers mods = KeyModifiers.None,
        bool appCursor = false, bool appKeypad = false)
        => KeyMapper.Map(key, mods, appCursor, appKeypad);

    [Fact]
    public void Space_PlainEmitsSpaceByte()
    {
        Assert.Equal(new byte[] { 0x20 }, Map(Key.Space));
    }

    [Fact]
    public void Space_CtrlEmitsNUL()
    {
        // Regression: Ctrl+Space used to return the literal space byte
        // because Key.Space was caught before the Ctrl-symbol mappings.
        Assert.Equal(new byte[] { 0x00 }, Map(Key.Space, KeyModifiers.Control));
    }

    [Fact]
    public void Space_ShiftStillEmitsSpaceByte()
    {
        Assert.Equal(new byte[] { 0x20 }, Map(Key.Space, KeyModifiers.Shift));
    }

    [Fact]
    public void Arrow_Unmodified_EmitsCsiLetter()
    {
        Assert.Equal(Encoding.ASCII.GetBytes("\x1b[A"), Map(Key.Up));
        Assert.Equal(Encoding.ASCII.GetBytes("\x1b[D"), Map(Key.Left));
    }

    [Fact]
    public void Arrow_AppCursorMode_EmitsSS3Letter()
    {
        Assert.Equal(Encoding.ASCII.GetBytes("\x1bOA"), Map(Key.Up, appCursor: true));
    }

    [Fact]
    public void Arrow_CtrlHeld_EmitsModifierEncodedCsi()
    {
        // Ctrl=4, mod = 1+4 = 5. Format: ESC [ 1 ; 5 A.
        Assert.Equal(Encoding.ASCII.GetBytes("\x1b[1;5A"),
            Map(Key.Up, KeyModifiers.Control));
    }

    [Fact]
    public void Arrow_ShiftHeld_EmitsModifierEncodedCsiRegardlessOfAppCursor()
    {
        // Shift=1, mod = 1+1 = 2. Modified form is CSI regardless of
        // app-cursor mode because SS3 can't carry a modifier param.
        Assert.Equal(Encoding.ASCII.GetBytes("\x1b[1;2A"),
            Map(Key.Up, KeyModifiers.Shift, appCursor: true));
    }

    [Fact]
    public void F1_Unmodified_EmitsSS3P()
    {
        Assert.Equal(Encoding.ASCII.GetBytes("\x1bOP"), Map(Key.F1));
    }

    [Fact]
    public void F1_AltHeld_EmitsCsiLetterWithMod()
    {
        // Alt=2, mod = 1+2 = 3.
        Assert.Equal(Encoding.ASCII.GetBytes("\x1b[1;3P"),
            Map(Key.F1, KeyModifiers.Alt));
    }

    [Fact]
    public void F5_Unmodified_EmitsCsiTilde()
    {
        Assert.Equal(Encoding.ASCII.GetBytes("\x1b[15~"), Map(Key.F5));
    }

    [Fact]
    public void F5_CtrlHeld_EmitsCsiCodeSemiModTilde()
    {
        Assert.Equal(Encoding.ASCII.GetBytes("\x1b[15;5~"),
            Map(Key.F5, KeyModifiers.Control));
    }

    [Fact]
    public void Enter_AlwaysEmitsCR()
    {
        Assert.Equal(new byte[] { 0x0D }, Map(Key.Enter));
        Assert.Equal(new byte[] { 0x0D }, Map(Key.Enter, KeyModifiers.Control));
    }

    [Fact]
    public void Tab_Unmodified_Is0x09()
    {
        Assert.Equal(new byte[] { 0x09 }, Map(Key.Tab));
    }

    [Fact]
    public void Tab_Shift_IsCBT()
    {
        Assert.Equal(Encoding.ASCII.GetBytes("\x1b[Z"),
            Map(Key.Tab, KeyModifiers.Shift));
    }

    [Fact]
    public void Backspace_EmitsDEL()
    {
        // DEL (0x7F) on every platform — matches xterm / iTerm2 /
        // gnome-terminal / ConPTY's idea of a backspace.
        Assert.Equal(new byte[] { 0x7F }, Map(Key.Back));
    }

    [Fact]
    public void CtrlLetter_MapsToC0()
    {
        // Ctrl+A..Z → 0x01..0x1A.
        Assert.Equal(new byte[] { 0x01 }, Map(Key.A, KeyModifiers.Control));
        Assert.Equal(new byte[] { 0x1A }, Map(Key.Z, KeyModifiers.Control));
    }

    [Fact]
    public void MetaAlone_ReturnsEmpty_BecauseItIsAnAppShortcut()
    {
        Assert.Empty(Map(Key.C, KeyModifiers.Meta));
    }
}
