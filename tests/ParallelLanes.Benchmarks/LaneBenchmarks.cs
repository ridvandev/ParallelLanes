using BenchmarkDotNet.Attributes;

namespace ParallelLanes.Benchmarks;

[MemoryDiagnoser]
public class LaneBenchmarks
{
    private static readonly TimeSpan WorkDuration = TimeSpan.FromMilliseconds(2);
    private static readonly string[] MixedLaneKeys = ["alpha", "beta", "gamma", "delta"];
    private readonly ParallelLaneRunnerOptions _options;

    public LaneBenchmarks()
    {
        var processors = Environment.ProcessorCount;
        var degree = Math.Max(1, Math.Min(processors, 8));
        _options = new ParallelLaneRunnerOptions
        {
            MaxDegreeOfParallelism = degree
        };
    }

    [Params(32, 128)] public int WorkItemCount { get; set; }

    [Benchmark(Baseline = true, Description = "Sequential single lane")]
    public async Task SequentialHighContention()
    {
        for (var i = 0; i < WorkItemCount; i++) await SimulatedWorkAsync(CancellationToken.None);
    }

    [Benchmark(Description = "Parallel lane runner high contention")]
    public async Task LaneRunnerHighContention()
    {
        await using var runner = new ParallelLaneRunner(_options);
        var tasks = new Task[WorkItemCount];
        for (var i = 0; i < WorkItemCount; i++) tasks[i] = runner.EnqueueAsync("shared", SimulatedWorkAsync);

        await Task.WhenAll(tasks);
    }

    [Benchmark(Description = "Parallel lane runner mixed lanes")]
    public async Task LaneRunnerMixedContention()
    {
        await using var runner = new ParallelLaneRunner(_options);
        var tasks = new Task[WorkItemCount];
        var lanes = MixedLaneKeys;
        for (var i = 0; i < WorkItemCount; i++)
        {
            var lane = lanes[i % lanes.Length];
            tasks[i] = runner.EnqueueAsync(lane, SimulatedWorkAsync);
        }

        await Task.WhenAll(tasks);
    }

    private static ValueTask SimulatedWorkAsync(CancellationToken ct)
    {
        return new ValueTask(Task.Delay(WorkDuration, ct));
    }
}