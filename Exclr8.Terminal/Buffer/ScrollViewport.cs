using System;

namespace Exclr8.Terminal.Buffer;

/// <summary>
/// Scroll position of the viewport within the combined scrollback +
/// live-screen space. <see cref="Offset"/> counts whole lines (0 = at
/// the bottom, positive = scrolled up into scrollback);
/// <see cref="PixelOffset"/> carries the sub-line pixel remainder so
/// trackpad / smooth-scroll wheel events accumulate fractionally
/// without integer-rounding jitter and cross into whole-line bumps
/// as they reach a cell height.
///
/// <para>Callers pass the current scrollback count + line height on
/// each mutation so the viewport can clamp without reaching back into
/// the buffer. Returns <c>true</c> from mutators when state actually
/// changed — lets the buffer bump its revision only on real motion.</para>
/// </summary>
public sealed class ScrollViewport
{
    /// <summary>Whole-line offset. 0 = bottom of live screen;
    /// <c>maxOffset</c> (== scrollback count) = top of oldest scrollback.</summary>
    public int Offset { get; private set; }

    /// <summary>Sub-line pixel remainder. 0 when aligned on a line
    /// boundary, strictly less than line height when mid-scroll.</summary>
    public double PixelOffset { get; private set; }

    /// <summary>Snap to a specific whole-line offset. Clamps to
    /// [0, <paramref name="maxOffset"/>] and clears the pixel remainder.
    /// Returns true if the position changed.</summary>
    public bool SetOffset(int offset, int maxOffset)
    {
        int clamped = Math.Clamp(offset, 0, maxOffset);
        if (clamped == Offset && PixelOffset == 0) return false;
        Offset = clamped;
        PixelOffset = 0;
        return true;
    }

    /// <summary>Add <paramref name="pixels"/> to the position
    /// (positive = scroll up into scrollback, negative = toward bottom).
    /// Crosses into whole-line <see cref="Offset"/> bumps as the
    /// accumulated distance reaches <paramref name="lineHeight"/>.
    /// Clamps against the scrollback bounds. Returns true if the
    /// position changed.</summary>
    public bool AddPixels(double pixels, double lineHeight, int maxOffset)
    {
        if (lineHeight <= 0) return false;
        double total  = Offset * lineHeight + PixelOffset + pixels;
        double maxTot = maxOffset * lineHeight;
        total = Math.Clamp(total, 0.0, maxTot);
        int   newOff = (int)(total / lineHeight);
        double newPx = total - newOff * lineHeight;
        if (newOff == Offset && Math.Abs(newPx - PixelOffset) <= 0.01) return false;
        Offset      = newOff;
        PixelOffset = newPx;
        return true;
    }

    /// <summary>Snap back to the bottom of the live screen. Returns
    /// true if the position changed.</summary>
    public bool Reset()
    {
        if (Offset == 0 && PixelOffset == 0) return false;
        Offset      = 0;
        PixelOffset = 0;
        return true;
    }

    /// <summary>Convert a visual row (0 = top of current viewport) to
    /// the corresponding absolute row (0 = oldest scrollback line).</summary>
    public int VisualToAbsRow(int visualRow, int scrollbackCount) =>
        scrollbackCount - Offset + visualRow;
}
