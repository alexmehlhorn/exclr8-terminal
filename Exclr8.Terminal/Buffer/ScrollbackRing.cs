using System;
using System.Collections;
using System.Collections.Generic;

namespace Exclr8.Terminal.Buffer;

/// <summary>
/// Indexable ring buffer backing a screen's scrollback history. Replaces
/// a <see cref="LinkedList{T}"/> so <c>this[i]</c> and enumeration are
/// O(1) — search walks the whole scrollback per keystroke, render walks
/// a viewport slice per frame, and the prior linked-list traversal from
/// the head made both path lengths O(index) instead of O(1).
/// </summary>
public sealed class ScrollbackRing : IEnumerable<TerminalCell[]>
{
    private TerminalCell[]?[] _buf;
    private int _head;
    private int _count;

    public ScrollbackRing(int capacity)
    {
        _buf = new TerminalCell[]?[Math.Max(1, capacity)];
    }

    public int Count => _count;

    public int Capacity
    {
        get => _buf.Length;
        set => Resize(value);
    }

    public TerminalCell[] this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _buf[(_head + index) % _buf.Length]!;
        }
        set
        {
            if ((uint)index >= (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(index));
            _buf[(_head + index) % _buf.Length] = value;
        }
    }

    /// <summary>Append a row; evicts the oldest when full. Returns the
    /// evicted row if eviction happened — callers can recycle its
    /// backing array as the new bottom-of-screen blank to avoid
    /// allocating on steady-state scroll.</summary>
    public TerminalCell[]? Add(TerminalCell[] row)
    {
        if (_buf.Length == 0) return null;
        if (_count < _buf.Length)
        {
            _buf[(_head + _count) % _buf.Length] = row;
            _count++;
            return null;
        }
        var evicted = _buf[_head]!;
        _buf[_head] = row;
        _head = (_head + 1) % _buf.Length;
        return evicted;
    }

    public void Clear()
    {
        Array.Clear(_buf, 0, _buf.Length);
        _head = 0;
        _count = 0;
    }

    public IEnumerator<TerminalCell[]> GetEnumerator()
    {
        for (int i = 0; i < _count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void Resize(int newCapacity)
    {
        newCapacity = Math.Max(1, newCapacity);
        if (newCapacity == _buf.Length) return;
        // Keep the newest `keep` rows; drop the oldest if shrinking.
        int keep = Math.Min(newCapacity, _count);
        int skip = _count - keep;
        var next = new TerminalCell[]?[newCapacity];
        for (int i = 0; i < keep; i++) next[i] = this[skip + i];
        _buf = next;
        _head = 0;
        _count = keep;
    }
}
