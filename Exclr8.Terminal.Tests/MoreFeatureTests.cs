using System.Linq;
using Exclr8.Terminal.Buffer;
using Xunit;
using static Exclr8.Terminal.Tests.TestHelpers;

namespace Exclr8.Terminal.Tests;

/// <summary>
/// Bell, OSC 7/9/133, dynamic palette, search options, lifecycle
/// events, link provider — coverage for the second xterm-parity batch.
/// </summary>
public class MoreFeatureTests
{
    [Fact]
    public void Bell_FiresOnBELByte()
    {
        var buf = NewBuffer();
        int count = 0;
        buf.Bell += (_, _) => count++;
        buf.FeedBytes(0x07);
        buf.FeedBytes(0x07);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Osc9_4_NormalProgress_FiresWithPercent()
    {
        var buf = NewBuffer();
        ProgressEventArgs? captured = null;
        buf.ProgressChanged += (_, e) => captured = e;
        buf.Feed(OSC + "9;4;1;42" + ST);
        Assert.NotNull(captured);
        Assert.Equal(ProgressState.Normal, captured!.State);
        Assert.Equal(42, captured.Percent);
    }

    [Fact]
    public void Osc9_4_RemoveState_HasNoPercent()
    {
        var buf = NewBuffer();
        ProgressEventArgs? captured = null;
        buf.ProgressChanged += (_, e) => captured = e;
        buf.Feed(OSC + "9;4;0" + ST);
        Assert.NotNull(captured);
        Assert.Equal(ProgressState.Remove, captured!.State);
        Assert.Null(captured.Percent);
    }

    [Fact]
    public void Osc9_4_ErrorState_PercentClampedTo100()
    {
        var buf = NewBuffer();
        ProgressEventArgs? captured = null;
        buf.ProgressChanged += (_, e) => captured = e;
        buf.Feed(OSC + "9;4;2;250" + ST);
        Assert.NotNull(captured);
        Assert.Equal(ProgressState.Error, captured!.State);
        Assert.Equal(100, captured.Percent);
    }

    [Fact]
    public void Osc4_PaletteSet_SurfacesAsDynamicOverride()
    {
        var buf = NewBuffer();
        // Set palette index 5 to pure red via the rgb: format.
        buf.Feed(OSC + "4;5;rgb:ff/00/00" + ST);
        Assert.True(buf.TryGetDynamicPaletteColor(5, out uint rgb));
        Assert.Equal(0xFF0000u, rgb);
        // Untouched index returns false.
        Assert.False(buf.TryGetDynamicPaletteColor(6, out _));
    }

    [Fact]
    public void Osc10_SetsDefaultForeground_ExplicitFlag()
    {
        var buf = NewBuffer();
        Assert.False(buf.DefaultForegroundExplicit);
        buf.Feed(OSC + "10;rgb:12/34/56" + ST);
        Assert.True(buf.DefaultForegroundExplicit);
        Assert.Equal(0x123456u, buf.DefaultForegroundRgb);
    }

    [Fact]
    public void Osc11_SetsDefaultBackground_FiresPaletteChanged()
    {
        var buf = NewBuffer();
        int fired = 0;
        buf.PaletteChanged += (_, _) => fired++;
        buf.Feed(OSC + "11;#ff0000" + ST);
        Assert.True(buf.DefaultBackgroundExplicit);
        Assert.True(fired > 0);
    }

    [Fact]
    public void SelectionChanged_FiresOnStartAndClear()
    {
        var buf = NewBuffer(20, 5);
        buf.Feed("hi");
        int fired = 0;
        TerminalSelection? last = null;
        buf.SelectionChanged += (_, s) => { fired++; last = s; };
        buf.SelectAll();
        Assert.Equal(1, fired);
        Assert.NotNull(last);
        buf.ClearSelection();
        Assert.Equal(2, fired);
        Assert.Null(last);
    }

    [Fact]
    public void CursorMoved_FiresOncePerWriteWhenCursorChanges()
    {
        var buf = NewBuffer(20, 5);
        int fired = 0;
        buf.CursorMoved += (_, _) => fired++;
        buf.Feed("hello");
        Assert.Equal(1, fired);
        buf.Feed(""); // empty write doesn't move cursor → no event
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Search_CaseSensitive_OnlyMatchesExactCase()
    {
        var buf = NewBuffer(40, 4);
        buf.Feed("Hello hello HELLO");
        var snap = buf.SnapshotRows();
        var matches = TerminalBuffer.ScanMatches(snap, "Hello",
            new SearchOptions { CaseSensitive = true },
            System.Threading.CancellationToken.None);
        Assert.Single(matches);
    }

    [Fact]
    public void Search_WholeWord_RequiresWordBoundaries()
    {
        var buf = NewBuffer(40, 4);
        buf.Feed("test testing tested test");
        var snap = buf.SnapshotRows();
        var matches = TerminalBuffer.ScanMatches(snap, "test",
            new SearchOptions { WholeWord = true },
            System.Threading.CancellationToken.None);
        // "test" appears twice as a whole word; "testing" and "tested"
        // are excluded.
        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void Search_Regex_FindsPatternMatches()
    {
        var buf = NewBuffer(40, 4);
        buf.Feed("error code 42, error code 17, success");
        var snap = buf.SnapshotRows();
        var matches = TerminalBuffer.ScanMatches(snap, @"error code \d+",
            new SearchOptions { Regex = true },
            System.Threading.CancellationToken.None);
        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void Search_InvalidRegex_ReturnsNoMatches()
    {
        var buf = NewBuffer(40, 4);
        buf.Feed("abc");
        var snap = buf.SnapshotRows();
        var matches = TerminalBuffer.ScanMatches(snap, "[unclosed",
            new SearchOptions { Regex = true },
            System.Threading.CancellationToken.None);
        Assert.Empty(matches);
    }

    [Fact]
    public void WordSeparators_DefaultExcludesSlash_ForWordSelection()
    {
        var buf = NewBuffer(40, 4);
        buf.Feed("/etc/passwd");
        // Default separators include '/', so double-clicking 'e' in
        // "/etc/" should select just "etc", not the whole path.
        buf.SelectWord(0, 2); // pointer over 't' in "etc"
        Assert.NotNull(buf.Selection);
        Assert.Equal("etc", buf.GetSelectedText());
    }

    [Fact]
    public void WordSeparators_HostOverride_ExtendsSelectionAcrossSlash()
    {
        var buf = NewBuffer(40, 4);
        buf.WordSeparators = " \t"; // narrow set: only whitespace
        buf.Feed("/etc/passwd");
        buf.SelectWord(0, 2);
        Assert.Equal("/etc/passwd", buf.GetSelectedText());
    }

    [Fact]
    public void WebLinkProvider_MatchesHttpUrls()
    {
        var p = new WebLinkProvider();
        var links = p.Provide("see https://example.com for details").ToList();
        Assert.Single(links);
        Assert.Equal("https://example.com", links[0].Url);
    }

    [Fact]
    public void WebLinkProvider_StripsTrailingSentencePunctuation()
    {
        var p = new WebLinkProvider();
        var links = p.Provide("visit https://example.com/path. Yes!").ToList();
        Assert.Single(links);
        Assert.Equal("https://example.com/path", links[0].Url);
    }

    [Fact]
    public void RowText_AstralRune_OccupiesTwoStringIndicesOneCellColumn()
    {
        // Build a small live row containing 😀 (U+1F600, astral, wide)
        // followed by an ASCII URL. Without the colMap, link spans
        // would be off by one column to the right.
        var buf = NewBuffer(20, 4);
        buf.Feed("\U0001f600 https://example.com");
        var cells = buf.GetVisibleRow(0);
        string rowText = RowText.Build(cells, out int[] colMap);
        // string indices 0-1 are the surrogate pair → cell 0
        Assert.Equal(0, colMap[0]);
        Assert.Equal(0, colMap[1]);
        // index 2 is the wide-cell continuation slot → cell 1
        Assert.Equal(1, colMap[2]);
        // index 3 = space → cell 2
        Assert.Equal(2, colMap[3]);
        // index 4 = 'h' → cell 3
        Assert.Equal(3, colMap[4]);
        // The URL match in rowText starts at the 'h'.
        int hIndex = rowText.IndexOf("https");
        Assert.True(hIndex > 0);
        // The cell column is colMap[hIndex] = 3, NOT 4.
        Assert.Equal(3, colMap[hIndex]);
    }

    [Fact]
    public void Select_ProgrammaticSelection_ReturnsExpectedText()
    {
        var buf = NewBuffer(20, 4);
        buf.Feed("hello");
        buf.Select(0, 0, 0, 4);
        Assert.Equal("hello", buf.GetSelectedText());
    }
}
