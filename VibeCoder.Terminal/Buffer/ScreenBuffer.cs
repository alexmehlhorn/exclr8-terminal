using System;
using System.Collections.Generic;

namespace VibeCoder.Terminal.Buffer;

/// <summary>
/// One of the two screen buffers a terminal keeps — the primary (with
/// scrollback) or the alternate (no scrollback, used by full-screen
/// apps like vim/htop). Owns its visible row array, scrollback ring,
/// and the primitive scroll operations.
/// </summary>
public sealed class ScreenBuffer
{
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int ScrollbackLimit { get; set; }

    public LinkedList<TerminalCell[]> Scrollback { get; } = new();
    private readonly List<TerminalCell[]> _rows = new();

    public ScreenBuffer(int cols, int rows, int scrollbackLimit)
    {
        Cols = cols;
        Rows = rows;
        ScrollbackLimit = scrollbackLimit;
        for (int i = 0; i < rows; i++) _rows.Add(new TerminalCell[cols]);
    }

    public TerminalCell[] GetRow(int r) => _rows[r];

    public void Resize(int cols, int rows)
    {
        if (cols != Cols)
        {
            // Column change: allocate new row arrays and copy the
            // common prefix. We replace entries in the list so the
            // references we give out via GetRow() stay authoritative.
            for (int r = 0; r < _rows.Count; r++)
            {
                var old = _rows[r];
                var next = new TerminalCell[cols];
                Array.Copy(old, next, Math.Min(old.Length, cols));
                _rows[r] = next;
            }

            // Scrollback is a LinkedList — walk and replace nodes in
            // place to keep ordering.
            var node = Scrollback.First;
            while (node != null)
            {
                var old = node.Value;
                var next = new TerminalCell[cols];
                Array.Copy(old, next, Math.Min(old.Length, cols));
                node.Value = next;
                node = node.Next;
            }
            Cols = cols;
        }

        if (rows > Rows)
        {
            for (int i = Rows; i < rows; i++) _rows.Add(new TerminalCell[Cols]);
        }
        else if (rows < Rows)
        {
            int extra = Rows - rows;
            for (int i = 0; i < extra; i++)
            {
                PushScrollback(_rows[0]);
                _rows.RemoveAt(0);
            }
        }
        Rows = rows;
    }

    /// <summary>Scroll up: topmost row goes to scrollback, blank row
    /// appended at the bottom.</summary>
    public void ScrollUp()
    {
        PushScrollback(_rows[0]);
        _rows.RemoveAt(0);
        _rows.Add(new TerminalCell[Cols]);
    }

    /// <summary>Scroll down: blank row inserted at the top, bottom row
    /// dropped (not added to scrollback — this is a DECSET 6 / RI
    /// behaviour, not an output scroll).</summary>
    public void ScrollDown()
    {
        _rows.Insert(0, new TerminalCell[Cols]);
        _rows.RemoveAt(_rows.Count - 1);
    }

    public void InsertLines(int at, int n)
    {
        if (at < 0 || at >= Rows) return;
        for (int i = 0; i < n; i++)
        {
            _rows.Insert(at, new TerminalCell[Cols]);
            _rows.RemoveAt(_rows.Count - 1);
        }
    }

    public void DeleteLines(int at, int n)
    {
        if (at < 0 || at >= Rows) return;
        for (int i = 0; i < n; i++)
        {
            _rows.RemoveAt(at);
            _rows.Add(new TerminalCell[Cols]);
        }
    }

    public void Clear()
    {
        for (int r = 0; r < _rows.Count; r++)
        {
            var row = _rows[r];
            Array.Clear(row, 0, row.Length);
        }
    }

    private void PushScrollback(TerminalCell[] row)
    {
        if (ScrollbackLimit <= 0) return;
        Scrollback.AddLast(row);
        while (Scrollback.Count > ScrollbackLimit) Scrollback.RemoveFirst();
    }
}
