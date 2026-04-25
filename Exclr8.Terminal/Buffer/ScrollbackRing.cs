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
    private bool[] _wrapped;
    private int _head;
    private int _count;
    private long _evicted;

    /// <summary>Total number of rows that have been dropped from the
    /// ring since construction — through eviction, capacity shrink,
    /// or explicit clear. Markers use this as a stable, monotonically
    /// increasing reference so their Line getter can detect when their
    /// anchored content has scrolled off the top.</summary>
    public long EvictionCount => _evicted;

    public ScrollbackRing(int capacity)
    {
        int cap = Math.Max(1, capacity);
        _buf = new TerminalCell[]?[cap];
        _wrapped = new bool[cap];
    }

    public bool IsWrapped(int index)
    {
        if ((uint)index >= (uint)_count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return _wrapped[(_head + index) % _wrapped.Length];
    }

    public void SetWrapped(int index, bool value)
    {
        if ((uint)index >= (uint)_count)
            throw new ArgumentOutOfRangeException(nameof(index));
        _wrapped[(_head + index) % _wrapped.Length] = value;
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
    public TerminalCell[]? Add(TerminalCell[] row, bool wrapped = false)
    {
        if (_buf.Length == 0) return null;
        if (_count < _buf.Length)
        {
            int slot = (_head + _count) % _buf.Length;
            _buf[slot] = row;
            _wrapped[slot] = wrapped;
            _count++;
            return null;
        }
        var evicted = _buf[_head]!;
        _buf[_head] = row;
        _wrapped[_head] = wrapped;
        _head = (_head + 1) % _buf.Length;
        _evicted++;
        return evicted;
    }

    public void Clear()
    {
        _evicted += _count;
        Array.Clear(_buf, 0, _buf.Length);
        Array.Clear(_wrapped, 0, _wrapped.Length);
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
        var next   = new TerminalCell[]?[newCapacity];
        var nextW  = new bool[newCapacity];
        for (int i = 0; i < keep; i++)
        {
            next[i]  = this[skip + i];
            nextW[i] = IsWrapped(skip + i);
        }
        _buf = next;
        _wrapped = nextW;
        _head = 0;
        _count = keep;
        _evicted += skip;
    }
}
