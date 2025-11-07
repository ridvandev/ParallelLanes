namespace ParallelLanes;

/// <summary>
///     Configures the behaviour of <see cref="ParallelLaneRunner" />.
/// </summary>
public sealed class ParallelLaneRunnerOptions
{
    private IEqualityComparer<string> _laneKeyComparer = StringComparer.Ordinal;
    private int _maxDegreeOfParallelism = Environment.ProcessorCount;

    /// <summary>
    ///     Limits the number of concurrently running work items. Defaults to the processor count.
    /// </summary>
    public int MaxDegreeOfParallelism
    {
        get => _maxDegreeOfParallelism;
        set => _maxDegreeOfParallelism = value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Value must be greater than or equal to 1.");
    }

    /// <summary>
    ///     Comparer used to group work items into lanes. Defaults to <see cref="StringComparer.Ordinal" />.
    /// </summary>
    public IEqualityComparer<string> LaneKeyComparer
    {
        get => _laneKeyComparer;
        set => _laneKeyComparer = value ?? throw new ArgumentNullException(nameof(value));
    }
}