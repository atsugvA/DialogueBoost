using System;
using System.Collections.Generic;
using Jellyfin.Plugin.DialogueBoost.ScheduledTasks;
using MediaBrowser.Model.Tasks;
using Xunit;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

public class DailyRunCatchUpTests
{
    private static readonly DateTime Now = new(2026, 8, 27, 18, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyList<TaskTriggerInfo> Daily() =>
        new[] { new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(2).Ticks } };

    private static IReadOnlyList<TaskTriggerInfo> Startup() =>
        new[] { new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger } };

    [Fact]
    public void ATaskWithNoTriggersIsNeverCaughtUp()
    {
        Assert.False(DailyRunCatchUp.WasMissed(Array.Empty<TaskTriggerInfo>(), null, Now));
        Assert.False(DailyRunCatchUp.WasMissed(null, null, Now));
    }

    [Fact]
    public void ATaskScheduledSomeOtherWayIsNotADailyTask()
    {
        Assert.False(DailyRunCatchUp.WasMissed(Startup(), null, Now));
    }

    [Fact]
    public void ADailyTaskThatHasNeverRunIsOverdue()
    {
        Assert.True(DailyRunCatchUp.WasMissed(Daily(), null, Now));
    }

    [Fact]
    public void ADailyTaskThatRanTodayIsNotOverdue()
    {
        Assert.False(DailyRunCatchUp.WasMissed(Daily(), Now.AddHours(-2), Now));
        Assert.False(DailyRunCatchUp.WasMissed(Daily(), Now.AddHours(-23.5), Now));
    }

    [Fact]
    public void ADailyTaskThatMissedItsSlotIsOverdue()
    {
        Assert.True(DailyRunCatchUp.WasMissed(Daily(), Now.AddHours(-24), Now));
        Assert.True(DailyRunCatchUp.WasMissed(Daily(), Now.AddDays(-3), Now));
    }
}
