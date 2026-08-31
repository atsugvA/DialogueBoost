using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DialogueBoost.Processing;
using Xunit;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

public class JobConcurrencyManagerTests
{
    [Fact]
    public async Task AcquireAsync_LimitsConcurrentExecution()
    {
        var manager = new JobConcurrencyManager(1);
        int activeCount = 0;
        int maxSimultaneous = 0;
        object lockObj = new();

        var tasks = new Task[10];
        for (int i = 0; i < 10; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                using (await manager.AcquireAsync(CancellationToken.None))
                {
                    lock (lockObj)
                    {
                        activeCount++;
                        if (activeCount > maxSimultaneous)
                        {
                            maxSimultaneous = activeCount;
                        }
                    }

                    await Task.Delay(50);

                    lock (lockObj)
                    {
                        activeCount--;
                    }
                }
            });
        }

        await Task.WhenAll(tasks);

        // Default MaxConcurrentJobs in test environment is 1 (from default PluginConfiguration)
        Assert.Equal(1, maxSimultaneous);
    }

    /// <summary>
    /// Lowering the limit used to drop the count without taking the permits, so raising it again
    /// released permits the semaphore had never lost. Ten → one → ten left it holding nineteen.
    /// </summary>
    [Fact]
    public async Task LoweringThenRaisingTheLimit_DoesNotHandOutMoreSlotsThanTheSetting()
    {
        int configured = 10;
        var manager = new JobConcurrencyManager(() => configured);

        for (int cycle = 0; cycle < 4; cycle++)
        {
            configured = 1;
            (await manager.AcquireAsync(CancellationToken.None)).Dispose();
            configured = 10;
            (await manager.AcquireAsync(CancellationToken.None)).Dispose();
        }

        Assert.Equal(10, await MaxSimultaneous(manager, attempts: 32));
    }

    /// <summary>
    /// Enough cycles to push a leaking pool past the semaphore's ceiling of 32, where the old code
    /// threw <c>SemaphoreFullException</c> out of an acquire.
    /// </summary>
    [Fact]
    public async Task ManyLimitChanges_DoNotOverflowTheSemaphore()
    {
        int configured = 32;
        var manager = new JobConcurrencyManager(() => configured);

        for (int cycle = 0; cycle < 20; cycle++)
        {
            configured = 1;
            (await manager.AcquireAsync(CancellationToken.None)).Dispose();
            configured = 32;
            (await manager.AcquireAsync(CancellationToken.None)).Dispose();
        }

        Assert.Equal(32, await MaxSimultaneous(manager, attempts: 40));
    }

    /// <summary>A slot held by a running job is not taken away mid-encode.</summary>
    [Fact]
    public async Task LoweringTheLimit_LeavesARunningJobItsSlot()
    {
        int configured = 4;
        var manager = new JobConcurrencyManager(() => configured);

        using var held = await manager.AcquireAsync(CancellationToken.None);
        configured = 1;

        // The pool shrinks to the one slot in hand plus nothing free, so the next acquire waits.
        var queued = manager.AcquireAsync(CancellationToken.None);
        Assert.False(queued.IsCompleted);

        held.Dispose();
        (await queued).Dispose();
    }

    /// <summary>
    /// The shape the live failure was measured in: every permit held when the limit drops.
    /// </summary>
    /// <remarks>
    /// Shrinking used to take only the permits that happened to be free, from inside the acquire
    /// itself, so with eight jobs holding all eight it took nothing and the pool never moved — a
    /// live run lowered from 8 to 2 settled at 5 and stayed there for the remaining fifteen items.
    /// The eight releases then handed all eight parked callers a permit. Two is the answer.
    /// </remarks>
    [Fact]
    public async Task LoweringTheLimitWhileEveryPermitIsHeld_ReachesTheNewLimit()
    {
        int configured = 8;
        var manager = new JobConcurrencyManager(() => configured);
        using var cts = new CancellationTokenSource();

        var held = new List<IDisposable>();
        for (int i = 0; i < 8; i++)
        {
            held.Add(await manager.AcquireAsync(CancellationToken.None));
        }

        configured = 2;

        // Parked before anything is released, so nothing here depends on how the two races.
        var queued = new List<Task<IDisposable>>();
        for (int i = 0; i < 8; i++)
        {
            queued.Add(manager.AcquireAsync(cts.Token));
        }

        Assert.All(queued, task => Assert.False(task.IsCompleted));

        held.ForEach(slot => slot.Dispose());

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (queued.Count(task => task.IsCompleted) < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        // Anything else that was going to get through has had its chance by now.
        await Task.Delay(200);
        Assert.Equal(2, queued.Count(task => task.IsCompleted));

        // Cleanup only, and it asserts nothing: disposing one releaser frees a permit a still-parked
        // waiter can take before its cancellation lands, so which of the eight ends which way is a
        // race. The count above was taken before any of this and is what the test is about.
        cts.Cancel();
        await Task.WhenAll(queued.Select(async task =>
        {
            try
            {
                (await task).Dispose();
            }
            catch (OperationCanceledException)
            {
            }
        }));
    }

    /// <summary>
    /// And the debt is paid off, not carried: raising the limit again while it is owed must not
    /// leave the pool short of what the setting now asks for.
    /// </summary>
    [Fact]
    public async Task RaisingTheLimitAgain_CancelsWhatWasOwed()
    {
        int configured = 8;
        var manager = new JobConcurrencyManager(() => configured);

        var held = new List<IDisposable>();
        for (int i = 0; i < 8; i++)
        {
            held.Add(await manager.AcquireAsync(CancellationToken.None));
        }

        configured = 2;
        var parked = manager.AcquireAsync(CancellationToken.None);
        Assert.False(parked.IsCompleted);   // this acquire is what records the debt

        configured = 8;
        held.ForEach(slot => slot.Dispose());
        (await parked).Dispose();

        Assert.Equal(8, await MaxSimultaneous(manager, attempts: 16));
    }

    /// <summary>How many slots the pool will actually hand out at once, right now.</summary>
    private static async Task<int> MaxSimultaneous(JobConcurrencyManager manager, int attempts)
    {
        var held = new List<IDisposable>();
        for (int i = 0; i < attempts; i++)
        {
            var acquire = manager.AcquireAsync(CancellationToken.None);
            if (!acquire.IsCompleted)
            {
                break;
            }

            held.Add(await acquire);
        }

        int count = held.Count;
        held.ForEach(slot => slot.Dispose());
        return count;
    }
}
