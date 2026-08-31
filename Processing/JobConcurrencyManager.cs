using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.DialogueBoost.Processing;

/// <summary>
/// How many encodes may run at once, following the setting while a run is in progress.
/// </summary>
public class JobConcurrencyManager
{
    /// <summary>The most jobs the setting may ask for; also the semaphore's hard ceiling.</summary>
    private const int Ceiling = 32;

    private readonly object _lock = new();
    private readonly SemaphoreSlim _semaphore;
    private readonly Func<int> _configuredMaxJobs;

    /// <summary>
    /// How many permits the semaphore currently holds, free or taken. Kept in lockstep with the
    /// semaphore itself — see <see cref="EnsureCapacity"/>.
    /// </summary>
    private int _slots;

    /// <summary>
    /// How many of those permits are owed back: destroyed on release rather than returned to the
    /// pool. The effective limit is <c>_slots - _deficit</c>.
    /// </summary>
    private int _deficit;

    public JobConcurrencyManager() : this(GetConfiguredMaxJobs)
    {
    }

    public JobConcurrencyManager(int initialMaxJobs) : this(() => initialMaxJobs)
    {
    }

    public JobConcurrencyManager(Func<int> configuredMaxJobs)
    {
        _configuredMaxJobs = configuredMaxJobs;
        _slots = Clamp(configuredMaxJobs());
        _semaphore = new SemaphoreSlim(_slots, Ceiling);
    }

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        EnsureCapacity(Clamp(_configuredMaxJobs()));

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(this);
    }

    private static int Clamp(int value) => Math.Clamp(value, 1, Ceiling);

    private static int GetConfiguredMaxJobs()
    {
        try
        {
            return ReadPluginConfiguredMaxJobs();
        }
        catch
        {
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadPluginConfiguredMaxJobs()
    {
        return Plugin.Instance?.Configuration.MaxConcurrentJobs ?? 1;
    }

    /// <summary>
    /// Resizes the pool to match the setting.
    /// </summary>
    /// <remarks>
    /// The count is only ever changed together with the semaphore itself. It used to be lowered on
    /// its own — the permits stayed — so raising the limit again released permits the semaphore had
    /// never lost: lowering from 10 to 1 and back to 10 left it with 19, then 28, then a
    /// <c>SemaphoreFullException</c> past its ceiling of 32.
    /// <para>
    /// Shrinking never waits, and it does not have to succeed here. Taking only the permits that
    /// happen to be free is what left a run of eight stuck at five when the setting was lowered to
    /// two: this runs from inside <see cref="AcquireAsync"/>, so it competes with the very
    /// acquires it is trying to bound and loses whenever a job is holding a permit — which, during
    /// a run, is always. What it cannot take now it *owes*, and <see cref="ReleaseSlot"/> pays the
    /// debt down as jobs finish. So a lowered limit still takes effect as jobs finish rather than by
    /// stopping one mid-encode, but it does take effect.
    /// </para>
    /// </remarks>
    private void EnsureCapacity(int target)
    {
        lock (_lock)
        {
            // Growing: cancel what is owed before releasing anything, or the pool grows by permits
            // the next few releases would immediately destroy again.
            while (_slots - _deficit < target && _deficit > 0)
            {
                _deficit--;
            }

            while (_slots - _deficit < target)
            {
                _semaphore.Release();
                _slots++;
            }

            // Shrinking: free permits are destroyed now, held ones are owed.
            while (_slots - _deficit > target && _semaphore.Wait(0))
            {
                _slots--;
            }

            if (_slots - _deficit > target)
            {
                _deficit += _slots - _deficit - target;
            }
        }
    }

    /// <summary>
    /// Hands a permit back, or destroys it if the pool owes one.
    /// </summary>
    private void ReleaseSlot()
    {
        lock (_lock)
        {
            if (_deficit > 0)
            {
                _deficit--;
                _slots--;
                return;
            }
        }

        _semaphore.Release();
    }

    private sealed class Releaser : IDisposable
    {
        private readonly JobConcurrencyManager _owner;
        private bool _disposed;

        public Releaser(JobConcurrencyManager owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _owner.ReleaseSlot();
            }
        }
    }
}
