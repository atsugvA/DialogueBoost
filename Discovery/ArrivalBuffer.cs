using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.DialogueBoost.Discovery;

/// <summary>
/// Collects ids as they arrive and hands them over as a single batch once they stop arriving.
/// </summary>
/// <remarks>
/// A library scan raises its events in a burst — thousands of them, from several threads — and every
/// consumer wants the burst rather than the individual events. The timer here is a settle window,
/// not a poll: it is armed by an arrival and never runs while the library is quiet.
/// </remarks>
public sealed class ArrivalBuffer : IDisposable
{
    private readonly Func<TimeSpan> _settleWindow;
    private readonly Func<IReadOnlyCollection<Guid>, Task> _onSettled;
    private readonly HashSet<Guid> _pending = new();
    private readonly object _gate = new();
    private readonly Timer _timer;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrivalBuffer"/> class.
    /// </summary>
    /// <param name="settleWindow">
    /// How long arrivals must stay quiet before the batch is handed over. Asked per arrival rather
    /// than captured, so changing the setting takes effect without a restart.
    /// </param>
    /// <param name="onSettled">Receives each settled batch. Responsible for its own error handling.</param>
    public ArrivalBuffer(Func<TimeSpan> settleWindow, Func<IReadOnlyCollection<Guid>, Task> onSettled)
    {
        _settleWindow = settleWindow;
        _onSettled = onSettled;
        _timer = new Timer(OnTimerElapsed, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Records an arrival and restarts the settle window.
    /// </summary>
    public void Add(Guid id)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending.Add(id);
            _timer.Change(_settleWindow(), Timeout.InfiniteTimeSpan);
        }
    }

    // async void is deliberate and safe here: a timer callback has nowhere to return a Task to, and
    // the whole body is guarded.
    private async void OnTimerElapsed(object? state)
    {
        Guid[] batch;

        lock (_gate)
        {
            if (_disposed || _pending.Count == 0)
            {
                return;
            }

            batch = new Guid[_pending.Count];
            _pending.CopyTo(batch);
            _pending.Clear();
        }

        try
        {
            await _onSettled(batch).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The callback logs its own failures; this guard only stops a throwing callback from
            // taking the timer down with it and silently ending all future batches.
        }
    }

    /// <summary>
    /// Stops the buffer. Anything still pending is dropped — nothing is lost by that, because what a
    /// run processes is resolved from the library at run time, never from this buffer.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pending.Clear();
        }

        _timer.Dispose();
    }
}
