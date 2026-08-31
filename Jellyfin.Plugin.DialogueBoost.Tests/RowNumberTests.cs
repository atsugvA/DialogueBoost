using Jellyfin.Plugin.DialogueBoost.Selection;
using Xunit;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

public class RowNumberTests
{
    [Fact]
    public void AnEpisodeReadsAsItsNumber()
    {
        Assert.Equal("E3", RowNumber.Format(3, null));
        Assert.Equal("E12", RowNumber.Format(12, null));
    }

    [Fact]
    public void AFileHoldingTwoEpisodesReadsAsBoth()
    {
        Assert.Equal("E3–4", RowNumber.Format(3, 4));
    }

    [Fact]
    public void AnEndThatAddsNothingIsLeftOff()
    {
        // An end at or below the number describes one episode, not a span, and "E3–3" would be noise.
        Assert.Equal("E3", RowNumber.Format(3, 3));
        Assert.Equal("E3", RowNumber.Format(3, 2));
    }

    [Fact]
    public void ARowJellyfinCouldNotNumberIsNotNumbered()
    {
        // Nothing is invented for it — the page shows the name on its own and the row sorts last.
        Assert.Null(RowNumber.Format(null, null));
        Assert.Null(RowNumber.Format(null, 4));
    }
}
