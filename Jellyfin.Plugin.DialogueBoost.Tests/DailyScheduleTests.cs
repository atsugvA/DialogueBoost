using System;
using Jellyfin.Plugin.DialogueBoost.ScheduledTasks;
using Xunit;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

public class DailyScheduleTests
{
    [Theory]
    [InlineData("00:00", 0, 0)]
    [InlineData("02:00", 2, 0)]
    [InlineData("04:15", 4, 15)]
    [InlineData("23:59", 23, 59)]
    public void ATimeOfDayIsReadAsWritten(string text, int hours, int minutes)
    {
        Assert.True(DailySchedule.TryParseTimeOfDay(text, out var parsed));
        Assert.Equal(new TimeSpan(hours, minutes, 0), parsed);
        Assert.Equal(text, DailySchedule.Format(parsed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2:00")]
    [InlineData("25:00")]
    [InlineData("02:60")]
    [InlineData("2 AM")]
    [InlineData("02:00:00")]
    [InlineData("-01:00")]
    public void AnythingElseIsRefusedRatherThanGuessedAt(string? text)
    {
        // A schedule read wrongly runs the task at the wrong time every night, silently — so the
        // parse is exact, and the caller answers 400 rather than picking something plausible.
        Assert.False(DailySchedule.TryParseTimeOfDay(text, out var parsed));
        Assert.Equal(default, parsed);
    }
}
