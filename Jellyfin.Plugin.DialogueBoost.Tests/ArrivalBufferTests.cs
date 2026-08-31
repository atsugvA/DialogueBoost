using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.DialogueBoost.Discovery;
using Xunit;

namespace Jellyfin.Plugin.DialogueBoost.Tests;

public class ArrivalBufferTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(150);

    /// <summary>Collects the batches a buffer hands over, and lets a test await the next one.</summary>
    private sealed class BatchSink
    {
        private readonly object _gate = new();
        private readonly List<IReadOnlyCollection<Guid>> _batches = new();
        private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<IReadOnlyCollection<Guid>> Batches
        {
            get { lock (_gate) { return _batches.ToList(); } }
        }

        public Task Receive(IReadOnlyCollection<Guid> batch)
        {
            TaskCompletionSource signal;
            lock (_gate)
            {
                _batches.Add(batch);
                signal = _next;
                _next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            signal.TrySetResult();
            return Task.CompletedTask;
        }

        /// <summary>Waits for one more batch. Returns false on timeout instead of hanging the suite.</summary>
        public async Task<bool> WaitForBatchAsync()
        {
            Task next;
            lock (_gate)
            {
                next = _next.Task;
            }

            return await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) == next;
        }
    }

    [Fact]
    public async Task Add_CollapsesABurstIntoOneBatch()
    {
        var sink = new BatchSink();
        using var buffer = new ArrivalBuffer(() => Window, sink.Receive);

        var first = Guid.NewGuid();
        buffer.Add(first);
        buffer.Add(Guid.NewGuid());
        buffer.Add(Guid.NewGuid());
        buffer.Add(first); // the same item twice is one arrival

        Assert.True(await sink.WaitForBatchAsync(), "no batch was handed over");

        var batch = Assert.Single(sink.Batches);
        Assert.Equal(3, batch.Count);
        Assert.Contains(first, batch);
    }

    [Fact]
    public async Task Add_StartsAFreshBatchAfterTheWindowElapses()
    {
        var sink = new BatchSink();
        using var buffer = new ArrivalBuffer(() => Window, sink.Receive);

        buffer.Add(Guid.NewGuid());
        Assert.True(await sink.WaitForBatchAsync(), "no first batch");

        buffer.Add(Guid.NewGuid());
        Assert.True(await sink.WaitForBatchAsync(), "no second batch");

        Assert.Equal(2, sink.Batches.Count);
        Assert.All(sink.Batches, b => Assert.Single(b));
    }

    [Fact]
    public async Task Idle_HandsOverNothing()
    {
        var sink = new BatchSink();
        using var buffer = new ArrivalBuffer(() => Window, sink.Receive);

        // The window is a settle timer armed by an arrival, not a poll — with no arrivals it must
        // never run.
        await Task.Delay(Window * 4).ConfigureAwait(false);

        Assert.Empty(sink.Batches);
    }

    [Fact]
    public async Task Dispose_DropsWhatIsStillPending()
    {
        var sink = new BatchSink();
        var buffer = new ArrivalBuffer(() => Window, sink.Receive);

        buffer.Add(Guid.NewGuid());
        buffer.Dispose();

        await Task.Delay(Window * 4).ConfigureAwait(false);

        Assert.Empty(sink.Batches);
    }

    [Fact]
    public async Task AThrowingCallbackDoesNotStopLaterBatches()
    {
        var sink = new BatchSink();
        bool thrown = false;
        using var buffer = new ArrivalBuffer(() => Window, batch =>
        {
            if (!thrown)
            {
                thrown = true;
                throw new InvalidOperationException("callback failed");
            }

            return sink.Receive(batch);
        });

        buffer.Add(Guid.NewGuid());
        await Task.Delay(Window * 3).ConfigureAwait(false);

        buffer.Add(Guid.NewGuid());
        Assert.True(await sink.WaitForBatchAsync(), "the buffer stopped after a failing callback");
    }
}
