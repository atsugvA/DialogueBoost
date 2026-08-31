using System;
using Jellyfin.Plugin.DialogueBoost.Selection;
using Xunit;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

public class NewLibrariesTests
{
    private static readonly DateTime Saved = new(2026, 8, 27, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ALibraryAddedAfterTheSelectionWasSavedIsNew()
    {
        Assert.True(NewLibraries.IsNew(Saved.AddMinutes(1), Saved));
    }

    [Fact]
    public void ALibraryThatWasAlreadyThereIsNot()
    {
        // The user saw it when they chose, and left it out on purpose.
        Assert.False(NewLibraries.IsNew(Saved.AddMinutes(-1), Saved));
        Assert.False(NewLibraries.IsNew(Saved, Saved));
    }

    [Fact]
    public void NothingIsNewUntilASelectionHasBeenSavedAtAll()
    {
        // With no selection there is nothing to be new *against*: the whole tree is unchosen, and
        // badging every library as new would say nothing.
        Assert.False(NewLibraries.IsNew(Saved.AddYears(1), null));
    }

    [Fact]
    public void TheComparisonIsMadeInUtc()
    {
        // DateCreated comes back as local time on some paths; comparing it raw would make a library
        // created an hour ago look like tomorrow's.
        var localCreated = new DateTime(2026, 8, 27, 17, 30, 0, DateTimeKind.Local);
        Assert.Equal(
            localCreated.ToUniversalTime() > Saved,
            NewLibraries.IsNew(localCreated, Saved));
    }
}
