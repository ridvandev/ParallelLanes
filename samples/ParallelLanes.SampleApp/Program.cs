using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ParallelLanes;

var services = new ServiceCollection();

services.AddLogging(builder =>
{
    builder.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss.fff ";
    });
});

services.AddParallelLaneRunner(options =>
{
    options.MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2);
    options.LaneKeyComparer = StringComparer.OrdinalIgnoreCase;
});

await using var provider = services.BuildServiceProvider();

var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Sample");
await using var runner = provider.GetRequiredService<IParallelLaneRunner>();

logger.LogInformation("Scheduling demo jobs...");

var categories = new[] { "blue", "yellow", "black", "black", "white", "white", "orange" };
var random = new Random();
var work = new List<Task>();

for (var i = 0; i < 50; i++)
{
    var lane = categories[i % categories.Length];
    var jobId = i + 1;

    work.Add(runner.EnqueueAsync(lane, ct => RunJobAsync(lane, jobId, ct), CancellationToken.None));
}

await Task.WhenAll(work);

logger.LogInformation("All jobs complete. Note how jobs in the same lane never overlapped.");

async ValueTask RunJobAsync(string lane, int jobId, CancellationToken ct)
{
    logger.LogInformation("Starting job {JobId} ({Lane})", jobId, lane);
    await Task.Delay(TimeSpan.FromMilliseconds(200 + random.Next(250)), ct);
    logger.LogInformation("Finished job {JobId} ({Lane})", jobId, lane);
}