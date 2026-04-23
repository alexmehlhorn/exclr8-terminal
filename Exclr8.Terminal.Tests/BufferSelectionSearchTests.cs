using Xunit;
using static Exclr8.Terminal.Tests.TestHelpers;

namespace Exclr8.Terminal.Tests;

/// <summary>
/// Selection + search semantics. Selection rows are relative to the
/// current viewport (flagged in TerminalBuffer). Search matches are
/// stored in absolute coordinates so they're stable while scrolling.
/// </summary>
public class BufferSelectionSearchTests
{
    // ---- Selection ----

    [Fact]
    public void StartSelection_CreatesCharacterSelection()
    {
        var buf = NewBuffer();
        buf.StartSelection(0, 0);
        Assert.NotNull(buf.Selection);
        Assert.Equal(Buffer.SelectionMode.Character, buf.Selection!.Mode);
    }

    [Fact]
    public void ExtendSelection_UpdatesEndpoint()
    {
        var buf = NewBuffer();
        buf.StartSelection(0, 0);
        buf.ExtendSelection(1, 5);
        var (r1, c1, r2, c2) = buf.Selection!.Normalized();
        Assert.Equal(0, r1);
        Assert.Equal(0, c1);
        Assert.Equal(1, r2);
        Assert.Equal(5, c2);
    }

    [Fact]
    public void SelectLine_CoversFullLine()
    {
        var buf = NewBuffer(10, 3);
        buf.Feed("hello world");
        buf.SelectLine(0);
        var (r1, c1, r2, c2) = buf.Selection!.Normalized();
        Assert.Equal(0, r1);
        Assert.Equal(0, c1);
        Assert.Equal(0, r2);
        Assert.Equal(9, c2);
    }

    [Fact]
    public void SelectWord_ExpandsToWordBoundary()
    {
        var buf = NewBuffer(20, 2);
        buf.Feed("hello world");
        buf.SelectWord(0, 2);
        var (r1, c1, r2, c2) = buf.Selection!.Normalized();
        Assert.Equal(0, c1);
        Assert.Equal(4, c2); // "hello" spans cols 0..4
    }

    [Fact]
    public void SelectAll_CoversFullViewport()
    {
        var buf = NewBuffer(8, 3);
        buf.SelectAll();
        var (r1, c1, r2, c2) = buf.Selection!.Normalized();
        Assert.Equal(0, r1);
        Assert.Equal(0, c1);
        Assert.Equal(2, r2);
        Assert.Equal(7, c2);
    }

    [Fact]
    public void ClearSelection_DropsIt()
    {
        var buf = NewBuffer();
        buf.StartSelection(0, 0);
        buf.ClearSelection();
        Assert.Null(buf.Selection);
    }

    [Fact]
    public void GetSelectedText_CharacterRange()
    {
        var buf = NewBuffer(10, 2);
        buf.Feed("hello");
        buf.StartSelection(0, 1);
        buf.ExtendSelection(0, 3);
        Assert.Equal("ell", buf.GetSelectedText());
    }

    [Fact]
    public void GetSelectedText_MultiLineSeparatedByNewline()
    {
        var buf = NewBuffer(5, 3);
        buf.Feed("ABCDE\r\nFGHIJ");
        buf.StartSelection(0, 2);
        buf.ExtendSelection(1, 1);
        // Row 0 from col 2 → "CDE"; row 1 col 0..1 → "FG"; joined with \n.
        Assert.Equal("CDE\nFG", buf.GetSelectedText());
    }

    // ---- Search ----

    [Fact]
    public void Search_EmptyNeedleClearsMatches()
    {
        var buf = NewBuffer();
        buf.Feed("hello");
        buf.Search("hello");
        Assert.Single(buf.SearchMatches);
        buf.Search("");
        Assert.Empty(buf.SearchMatches);
    }

    [Fact]
    public void Search_FindsMultipleOccurrencesCaseInsensitive()
    {
        var buf = NewBuffer(20, 2);
        buf.Feed("Hello hello HELLO");
        buf.Search("hello");
        Assert.Equal(3, buf.SearchMatches.Count);
    }

    [Fact]
    public void Search_NextMatchWraps()
    {
        var buf = NewBuffer(20, 2);
        buf.Feed("aa aa aa");
        buf.Search("aa");
        int start = buf.CurrentMatchIndex;
        buf.NextMatch();
        buf.NextMatch();
        buf.NextMatch(); // wrap around
        Assert.Equal(start, buf.CurrentMatchIndex);
    }

    [Fact]
    public void Search_PrevMatchWraps()
    {
        var buf = NewBuffer(20, 2);
        buf.Feed("aa aa aa");
        buf.Search("aa");
        int start = buf.CurrentMatchIndex;
        buf.PrevMatch();
        buf.PrevMatch();
        buf.PrevMatch();
        Assert.Equal(start, buf.CurrentMatchIndex);
    }

    [Fact]
    public void Search_ClearSearchResetsState()
    {
        var buf = NewBuffer();
        buf.Feed("hello");
        buf.Search("hello");
        buf.ClearSearch();
        Assert.Null(buf.SearchNeedle);
        Assert.Empty(buf.SearchMatches);
        Assert.Equal(-1, buf.CurrentMatchIndex);
    }

    [Fact]
    public void Search_ResultsSpanScrollback()
    {
        var buf = NewBuffer(10, 3);
        buf.ScrollbackLimit = 100;
        // Print 6 lines so first 3 roll into scrollback.
        buf.Feed("AAA needle\r\nBBB\r\nCCC\r\nDDD\r\nEEE needle\r\nFFF");
        buf.Search("needle");
        Assert.Equal(2, buf.SearchMatches.Count);
    }
}
