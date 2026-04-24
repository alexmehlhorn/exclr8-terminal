namespace Exclr8.Terminal.Buffer;

public enum SelectionMode { Character, Word, Line }

/// <summary>
/// Text selection in <b>absolute row coordinates</b>. Row 0 is the
/// oldest scrollback line; row <c>(ScrollbackCount + Rows - 1)</c> is
/// the bottom of the live screen. Anchored to content, not viewport —
/// scrolling after a selection keeps the highlight stuck to the same
/// bytes instead of drifting with the viewport. Endpoints are stored
/// as given; <see cref="Normalized"/> returns them in
/// top-left → bottom-right order for iteration.
/// </summary>
public record TerminalSelection(
    int StartRow, int StartCol,
    int EndRow,   int EndCol,
    SelectionMode Mode)
{
    public (int r1, int c1, int r2, int c2) Normalized()
    {
        if (StartRow < EndRow || (StartRow == EndRow && StartCol <= EndCol))
            return (StartRow, StartCol, EndRow, EndCol);
        return (EndRow, EndCol, StartRow, StartCol);
    }
}
