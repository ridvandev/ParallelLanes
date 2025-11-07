using System.Collections.Concurrent;

namespace ParallelLanes.Tests;

public class ParallelLaneRunnerTests
{
    [Fact]
    public async Task Tasks_with_same_lane_do_not_overlap()
    {
        await using var runner = new ParallelLaneRunner(new ParallelLaneRunnerOptions { MaxDegreeOfParallelism = 4 });

        var laneActivity = new ConcurrentDictionary<string, int>();
        var tasks = new List<Task>();
        var overlapDetected = false;
        var totalRunning = 0;
        var gate = new object();
        var maxTotal = 0;

        for (var i = 0; i < 20; i++)
        {
            var lane = i % 2 == 0 ? "A" : "B";
            tasks.Add(runner.EnqueueAsync(lane, ct => ExecuteLaneWorkAsync(lane, ct)));
        }

        await Task.WhenAll(tasks);

        Assert.False(overlapDetected);
        Assert.True(maxTotal > 1, "Parallelism was never observed.");
        Assert.All(laneActivity.Values, count => Assert.Equal(0, count));
        return;

        async ValueTask ExecuteLaneWorkAsync(string lane, CancellationToken ct)
        {
            var currentTotal = Interlocked.Increment(ref totalRunning);
            lock (gate)
            {
                if (currentTotal > maxTotal) maxTotal = currentTotal;
            }

            var active = laneActivity.AddOrUpdate(lane, 1, (_, existing) => existing + 1);
            if (active > 1) overlapDetected = true;

            await Task.Delay(20, ct);

            laneActivity.AddOrUpdate(lane, 0, (_, existing) => existing - 1);
            Interlocked.Decrement(ref totalRunning);
        }
    }

    [Fact]
    public async Task Tasks_waiting_on_same_lane_release_in_order()
    {
        await using var runner = new ParallelLaneRunner(new ParallelLaneRunnerOptions { MaxDegreeOfParallelism = 2 });

        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = runner.EnqueueAsync("A", FirstAsync);
        var second = runner.EnqueueAsync("A", SecondAsync);
        var other = runner.EnqueueAsync("B", OtherAsync);

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await otherStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(secondStarted.Task.IsCompleted);

        releaseFirst.TrySetResult();
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await Task.WhenAll(first, second, other);
        return;

        async ValueTask FirstAsync(CancellationToken ct)
        {
            firstStarted.TrySetResult();
            await releaseFirst.Task.WaitAsync(ct);
        }

        ValueTask SecondAsync(CancellationToken ct)
        {
            secondStarted.TrySetResult();
            return ValueTask.CompletedTask;
        }

        async ValueTask OtherAsync(CancellationToken ct)
        {
            otherStarted.TrySetResult();
            await Task.Delay(10, ct);
        }
    }

    [Fact]
    public async Task Dispose_cancels_waiting_items()
    {
        var options = new ParallelLaneRunnerOptions { MaxDegreeOfParallelism = 1 };
        var runner = new ParallelLaneRunner(options);

        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = runner.EnqueueAsync("lane", BlockingWorkAsync);

        var waitingOne = runner.EnqueueAsync("lane", _ => Task.CompletedTask);
        var waitingTwo = runner.EnqueueAsync("lane", _ => Task.CompletedTask);

        await Task.Delay(50);

        await runner.DisposeAsync();

        Assert.Equal(TaskStatus.Canceled, first.Status);
        Assert.Equal(TaskStatus.Canceled, waitingOne.Status);
        Assert.Equal(TaskStatus.Canceled, waitingTwo.Status);
        return;

        async ValueTask BlockingWorkAsync(CancellationToken ct)
        {
            await blocker.Task.WaitAsync(ct);
        }
    }
}