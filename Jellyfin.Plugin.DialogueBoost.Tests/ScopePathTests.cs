using System;
using Jellyfin.Plugin.DialogueBoost.Selection;
using Xunit;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

/// <summary>
/// The path is what a chosen row is stored as, so a path that does not survive a round trip is a
/// selection that silently covers the wrong thing.
/// </summary>
public class ScopePathTests
{
    private static readonly Guid Library = new("a656b907-eb3a-7353-2e40-e44b968d0225");

    [Fact]
    public void RootIsEmptyAndParsesBackToItself()
    {
        Assert.True(ScopePath.Root.IsRoot);
        Assert.Equal(string.Empty, ScopePath.Root.ToString());
        Assert.True(ScopePath.Parse(null).IsRoot);
        Assert.True(ScopePath.Parse(string.Empty).IsRoot);
        Assert.True(ScopePath.Parse("///").IsRoot);
    }

    [Fact]
    public void ALibraryAndAGroupRoundTrip()
    {
        var path = ScopePath.Root
            .Append(ScopeSegment.ForItem(Library))
            .Append("k:Series|sparrow")
            .Append("k:Season|season 2");

        Assert.Equal("i:a656b907eb3a73532e40e44b968d0225/k:Series|sparrow/k:Season|season 2", path.ToString());
        Assert.Equal(path, ScopePath.Parse(path.ToString()));
        Assert.Equal(3, ScopePath.Parse(path.ToString()).Segments.Count);
    }

    [Theory]
    [InlineData("Face/Off")]
    [InlineData("100% Wolf")]
    [InlineData("%2F")]
    [InlineData("a%2Fb/c%25d")]
    [InlineData("///")]
    public void ASeparatorInANameCannotInventALevel(string name)
    {
        var path = ScopePath.Root.Append(ScopeSegment.ForItem(Library)).Append("k:Movie|" + name);
        var parsed = ScopePath.Parse(path.ToString());

        Assert.Equal(2, parsed.Segments.Count);
        Assert.Equal("k:Movie|" + name, parsed.Segments[1]);
        Assert.Equal(path, parsed);
    }

    [Fact]
    public void ContainmentIsPrefix()
    {
        var library = ScopePath.Root.Append(ScopeSegment.ForItem(Library));
        var series = library.Append("k:Series|sparrow");
        var season = series.Append("k:Season|season 2");
        var other = library.Append("k:Series|sparrowhawk");

        Assert.True(library.Contains(season));
        Assert.True(series.Contains(season));

        // A row covers itself: that is what makes an exactly chosen row also a covered row.
        Assert.True(season.Contains(season));

        Assert.False(season.Contains(series));
        Assert.False(other.Contains(series));

        // Prefix on segments, never on the text: "sparrow" must not swallow "sparrowhawk".
        Assert.False(series.Contains(other));
    }

    [Fact]
    public void ItemSegmentsCarryTheirId()
    {
        Assert.Equal(Library, ScopeSegment.ItemId(ScopeSegment.ForItem(Library)));
        Assert.Equal(Guid.Empty, ScopeSegment.ItemId("k:Series|sparrow"));
        Assert.Equal(Guid.Empty, ScopeSegment.ItemId("i:not-a-guid"));
    }

    [Fact]
    public void ANameOnlyDiffersByCaseOrPaddingIsTheSameGroup()
    {
        Assert.Equal(ScopeSegment.Normalize("  Sparrow "), ScopeSegment.Normalize("sparrow"));
        Assert.NotEqual(ScopeSegment.Normalize("Sparrow"), ScopeSegment.Normalize("Sparrowhawk"));
    }
}
