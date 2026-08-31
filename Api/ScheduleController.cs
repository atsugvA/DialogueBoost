using System;
using System.Collections.Generic;
using Jellyfin.Plugin.DialogueBoost.Api.Models;
using Jellyfin.Plugin.DialogueBoost.ScheduledTasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DialogueBoost.Api;

/// <summary>
/// Reads and sets when the plugin's tasks run.
/// </summary>
/// <remarks>
/// The schedule is Jellyfin's, not the plugin's: this endpoint reads the tasks' triggers back from
/// the task manager and writes to them, so the page shows what will actually happen — including a
/// schedule somebody set from Jellyfin's own dashboard — rather than a setting of ours that only
/// looked like it was in charge.
/// </remarks>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Produces("application/json")]
public class ScheduleController : ControllerBase
{
    private readonly DailySchedule _schedule;

    public ScheduleController(DailySchedule schedule)
    {
        _schedule = schedule;
    }

    /// <summary>
    /// Gets when the two tasks are scheduled to run.
    /// </summary>
    [HttpGet("/Plugins/DialogueBoost/Schedule")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ScheduleDto> GetSchedule() => Ok(Describe());

    /// <summary>
    /// Sets the daily run of either task. A task left out of the request is left alone.
    /// </summary>
    [HttpPut("/Plugins/DialogueBoost/Schedule")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<ScheduleDto> PutSchedule([FromBody] ScheduleDto request)
    {
        if (request is null)
        {
            return BadRequest(new { Message = "A schedule is required." });
        }

        // Both times are read before either is written: a request that names one impossible time
        // changes nothing at all, rather than half a schedule.
        var wanted = new List<(string Key, TimeSpan? TimeOfDay)>();
        foreach (var (key, run) in new[]
                 {
                     (PluginTasks.NormalizeKey, request.Normalization),
                     (PluginTasks.CleanupKey, request.Cleanup)
                 })
        {
            if (run is null)
            {
                continue;
            }

            if (!TryReadTime(run, out var timeOfDay, out string? error))
            {
                return BadRequest(new { Message = error });
            }

            wanted.Add((key, timeOfDay));
        }

        foreach (var (key, timeOfDay) in wanted)
        {
            _schedule.Write(key, timeOfDay);
        }

        // The stored schedule, read back — never the request echoed, so a task Jellyfin does not
        // have registered is visible as unscheduled instead of as saved.
        return Ok(Describe());
    }

    /// <summary>
    /// A run with no time of day would mean midnight, which is a real answer — so an absent time is
    /// only accepted when the run is off.
    /// </summary>
    private static bool TryReadTime(DailyRunDto run, out TimeSpan? timeOfDay, out string? error)
    {
        timeOfDay = null;
        error = null;

        if (!run.Enabled)
        {
            return true;
        }

        if (!DailySchedule.TryParseTimeOfDay(run.TimeOfDay, out var parsed))
        {
            error = $"'{run.TimeOfDay}' is not a time of day. Give it as HH:mm, from 00:00 to 23:59.";
            return false;
        }

        timeOfDay = parsed;
        return true;
    }

    private ScheduleDto Describe() => new()
    {
        Normalization = Describe(PluginTasks.NormalizeKey),
        Cleanup = Describe(PluginTasks.CleanupKey)
    };

    private DailyRunDto Describe(string taskKey)
    {
        var at = _schedule.Read(taskKey);

        return new DailyRunDto
        {
            Enabled = at is not null,
            TimeOfDay = at is TimeSpan time ? DailySchedule.Format(time) : null
        };
    }
}
