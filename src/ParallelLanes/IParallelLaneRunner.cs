namespace ParallelLanes;

/// <summary>
///     Schedules work for parallel execution while preventing overlap among items sharing the same lane key.
/// </summary>
public interface IParallelLaneRunner : IAsyncDisposable
{
    /// <summary>
    ///     The maximum concurrent work items the runner executes.
    /// </summary>
    int MaxDegreeOfParallelism { get; }

    /// <summary>
    ///     Enqueues work that cooperates with other items using the same <paramref name="laneKey" />.
    ///     The returned task completes when the work delegate finishes running.
    /// </summary>
    Task EnqueueAsync(string laneKey, Func<CancellationToken, ValueTask> work,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Convenience overload for Task-returning delegates.
    /// </summary>
    Task EnqueueAsync(string laneKey, Func<CancellationToken, Task> work,
        CancellationToken cancellationToken = default);
}