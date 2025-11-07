using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ParallelLanes;

/// <summary>
///     Coordinates parallel execution while preventing overlap for work items sharing a lane key.
/// </summary>
public sealed class ParallelLaneRunner : IParallelLaneRunner
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, LaneState> _lanes;
    private readonly ILogger<ParallelLaneRunner>? _logger;
    private readonly ConcurrentQueue<WorkItem> _readyQueue = new();
    private readonly SemaphoreSlim _readySignal = new(0);
    private readonly object _sync = new();
    private readonly Task[] _workers;
    private volatile bool _accepting = true;
    private bool _disposed;

    /// <summary>
    ///     Creates a runner using default options.
    /// </summary>
    public ParallelLaneRunner()
        : this(new ParallelLaneRunnerOptions())
    {
    }

    /// <summary>
    ///     Creates a runner using the supplied options instance.
    /// </summary>
    public ParallelLaneRunner(ParallelLaneRunnerOptions options, ILogger<ParallelLaneRunner>? logger = null)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));

        _logger = logger;
        Options = new ParallelLaneRunnerOptions
        {
            MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
            LaneKeyComparer = options.LaneKeyComparer
        };

        _lanes = new Dictionary<string, LaneState>(Options.LaneKeyComparer);
        _workers = StartWorkers(Options.MaxDegreeOfParallelism);
    }

    /// <summary>
    ///     Creates a runner using options provided through dependency injection.
    /// </summary>
    public ParallelLaneRunner(IOptions<ParallelLaneRunnerOptions> options, ILogger<ParallelLaneRunner>? logger = null)
        : this(options.Value ?? throw new ArgumentNullException(nameof(options)), logger)
    {
    }

    private ParallelLaneRunnerOptions Options { get; }

    /// <inheritdoc />
    public int MaxDegreeOfParallelism => Options.MaxDegreeOfParallelism;

    /// <inheritdoc />
    public Task EnqueueAsync(string laneKey, Func<CancellationToken, ValueTask> work,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(laneKey))
            throw new ArgumentException("Lane key must be provided.", nameof(laneKey));

        if (work is null) throw new ArgumentNullException(nameof(work));

        cancellationToken.ThrowIfCancellationRequested();

        EnsureNotDisposed();

        var workItem = new WorkItem(laneKey, work, cancellationToken);
        Schedule(workItem);
        return workItem.Completion.Task;
    }

    /// <inheritdoc />
    public Task EnqueueAsync(string laneKey, Func<CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
    {
        if (work is null) throw new ArgumentNullException(nameof(work));

        return EnqueueAsync(laneKey, ct => new ValueTask(work(ct)), cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _disposed = true;
        _accepting = false;

        _cts.Cancel();
        _readySignal.Release(_workers.Length);

        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Parallel lane runner worker stopped with an exception.");
        }

        DrainPendingWorkItems();

        _readySignal.Dispose();
        _cts.Dispose();
    }

    private Task[] StartWorkers(int count)
    {
        var tasks = new Task[count];
        for (var i = 0; i < tasks.Length; i++) tasks[i] = Task.Run(() => WorkerLoopAsync(_cts.Token));

        return tasks;
    }

    private async Task WorkerLoopAsync(CancellationToken token)
    {
        while (true)
        {
            try
            {
                await _readySignal.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!_readyQueue.TryDequeue(out var workItem)) continue;

            if (workItem.CallerCancellationToken.IsCancellationRequested)
            {
                workItem.Completion.TrySetCanceled(workItem.CallerCancellationToken);
                ReleaseLane(workItem);
                continue;
            }

            await ExecuteWorkItemAsync(workItem, token).ConfigureAwait(false);
        }
    }

    private async Task ExecuteWorkItemAsync(WorkItem workItem, CancellationToken runnerToken)
    {
        CancellationTokenSource? linkedSource = null;
        var effectiveToken = runnerToken;

        if (workItem.CallerCancellationToken.CanBeCanceled)
        {
            linkedSource =
                CancellationTokenSource.CreateLinkedTokenSource(runnerToken, workItem.CallerCancellationToken);
            effectiveToken = linkedSource.Token;
        }

        try
        {
            await workItem.Callback(effectiveToken).ConfigureAwait(false);
            workItem.Completion.TrySetResult(true);
        }
        catch (OperationCanceledException ex)
        {
            if (workItem.CallerCancellationToken.IsCancellationRequested && !runnerToken.IsCancellationRequested)
                workItem.Completion.TrySetCanceled(workItem.CallerCancellationToken);
            else
                workItem.Completion.TrySetCanceled(runnerToken.IsCancellationRequested
                    ? runnerToken
                    : ex.CancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Parallel lane work item faulted for lane '{LaneKey}'.", workItem.LaneKey);
            workItem.Completion.TrySetException(ex);
        }
        finally
        {
            linkedSource?.Dispose();
            ReleaseLane(workItem);
        }
    }

    private void Schedule(WorkItem workItem)
    {
        WorkItem? readyToRun = null;

        lock (_sync)
        {
            if (!_accepting) throw new ObjectDisposedException(nameof(ParallelLaneRunner));

            if (!_lanes.TryGetValue(workItem.LaneKey, out var lane))
            {
                lane = new LaneState();
                _lanes[workItem.LaneKey] = lane;
            }

            if (!lane.IsRunning)
            {
                lane.IsRunning = true;
                readyToRun = workItem;
            }
            else
            {
                lane.Queue.Enqueue(workItem);
            }
        }

        if (readyToRun is not null) EnqueueReadyItem(readyToRun);
    }

    private void ReleaseLane(WorkItem completed)
    {
        WorkItem? next = null;

        lock (_sync)
        {
            if (_lanes.TryGetValue(completed.LaneKey, out var lane))
            {
                if (lane.Queue.Count > 0)
                {
                    next = lane.Queue.Dequeue();
                }
                else
                {
                    lane.IsRunning = false;
                    _lanes.Remove(completed.LaneKey);
                }
            }
        }

        if (next is not null) EnqueueReadyItem(next);
    }

    private void EnqueueReadyItem(WorkItem workItem)
    {
        _readyQueue.Enqueue(workItem);
        _readySignal.Release();
    }

    private void DrainPendingWorkItems()
    {
        while (_readyQueue.TryDequeue(out var pending)) pending.Completion.TrySetCanceled();

        lock (_sync)
        {
            foreach (var lane in _lanes.Values)
                while (lane.Queue.Count > 0)
                {
                    var queued = lane.Queue.Dequeue();
                    queued.Completion.TrySetCanceled();
                }

            _lanes.Clear();
        }
    }

    private void EnsureNotDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ParallelLaneRunner));
    }

    private sealed class LaneState
    {
        public bool IsRunning { get; set; }
        public Queue<WorkItem> Queue { get; } = new();
    }

    private sealed class WorkItem
    {
        public WorkItem(
            string laneKey,
            Func<CancellationToken, ValueTask> callback,
            CancellationToken callerCancellationToken)
        {
            LaneKey = laneKey;
            Callback = callback;
            CallerCancellationToken = callerCancellationToken;
            Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public string LaneKey { get; }
        public Func<CancellationToken, ValueTask> Callback { get; }
        public CancellationToken CallerCancellationToken { get; }
        public TaskCompletionSource<bool> Completion { get; }
    }
}