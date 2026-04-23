namespace VibeCoder.Terminal.Buffer;

public enum SelectionMode { Character, Word, Line }

/// <summary>
/// Text selection in terminal grid coordinates. Row 0 = top of the
/// current viewport; negative rows index into scrollback when offset
/// &gt; 0. Endpoints are stored as given; <see cref="Normalized"/> returns
/// them in top-left → bottom-right order for iteration.
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

    public bool Contains(int row, int col)
    {
        var (r1, c1, r2, c2) = Normalized();
        if (row < r1 || row > r2) return false;
        if (row == r1 && col < c1) return false;
        if (row == r2 && col > c2) return false;
        return true;
    }
}
