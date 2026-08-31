using Jellyfin.Plugin.DialogueBoost.ScheduledTasks;
using Xunit;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

public class CleanupRunOverrideTests
{
    [Fact]
    public void NothingIsAskedForByDefault()
    {
        Assert.False(new CleanupRunOverride().TakeIncludeExempt());
    }

    [Fact]
    public void TheInstructionIsSpentByTheRunThatTakesIt()
    {
        var over = new CleanupRunOverride();
        over.IncludeExemptOnNextRun();

        Assert.True(over.TakeIncludeExempt());
        Assert.False(over.TakeIncludeExempt());
    }

    [Fact]
    public void AskingTwiceStillOnlyAffectsOneRun()
    {
        var over = new CleanupRunOverride();
        over.IncludeExemptOnNextRun();
        over.IncludeExemptOnNextRun();

        Assert.True(over.TakeIncludeExempt());
        Assert.False(over.TakeIncludeExempt());
    }
}
