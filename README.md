# ParallelLanes

ParallelLanes is a lightweight lane-aware scheduler for .NET. It keeps unrelated work highly parallel while guaranteeing that items sharing the same key never overlap. Think of it as a parallel runner with automatic per-key mutexes.

## Features

- High-throughput execution with configurable worker count.
- Per-key serialization so conflicting jobs run in order without extra plumbing.
- First-class dependency-injection support for `IServiceCollection`.
- Targets .NET 6, .NET 7, and .NET 8 (net9+ applications fall back to the net8 build).
- Async-friendly APIs with cancellation and graceful disposal.

## Installation

Once the package is published:

```bash
dotnet add package ParallelLanes
```

Until then, clone the repo and reference `src/ParallelLanes/ParallelLanes.csproj` directly.

## Quick Start

```csharp
using ParallelLanes;

await using var runner = new ParallelLaneRunner(new ParallelLaneRunnerOptions
{
	MaxDegreeOfParallelism = Environment.ProcessorCount
});

var work = new List<Task>
{
	runner.EnqueueAsync("alpha", async ct =>
	{
		await Task.Delay(150, ct);
	}),
	runner.EnqueueAsync("beta", async ct =>
	{
		await Task.Delay(150, ct);
	}),
	runner.EnqueueAsync("alpha", async ct =>
	{
		// Runs only after the first "alpha" item finishes.
		await Task.Delay(150, ct);
	})
};

await Task.WhenAll(work);
```

## How It Works

- Each queued job declares a lane key. Jobs in the same lane execute sequentially; different lanes flow in parallel.
- The runner tracks active lanes and enqueues dependents without blocking other workers.
- When a worker finishes a job it immediately schedules the next waiting item in that lane.
- Cancellation tokens are honored for each queued task as well as for the runner itself.

## Configuration

```csharp
var options = new ParallelLaneRunnerOptions
{
	MaxDegreeOfParallelism = 4,
	LaneKeyComparer = StringComparer.OrdinalIgnoreCase
};
```

- `MaxDegreeOfParallelism`: upper bound on concurrent workers (default: logical processor count).
- `LaneKeyComparer`: comparer used to group lane keys (default: `StringComparer.Ordinal`).

## Dependency Injection

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ParallelLanes;

var services = new ServiceCollection();

services.AddLogging(builder => builder.AddSimpleConsole());
services.AddParallelLaneRunner(options =>
{
	options.MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2);
});

await using var provider = services.BuildServiceProvider();
await using var runner = provider.GetRequiredService<IParallelLaneRunner>();
```

The extension registers `IParallelLaneRunner` as a singleton and applies any custom options you supply.

## Cancellation and Lifetime

```csharp
var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

var task = runner.EnqueueAsync("io", async ct =>
{
	await DoWorkAsync(ct);
}, cts.Token);

// DisposeAsync cancels queued work and waits for workers to shut down.
await runner.DisposeAsync();
```

- Calling `EnqueueAsync` returns a task that completes when the job finishes or propagates the failure.
- Cancel the provided token to drop queued work before it starts.
- Disposing the runner cancels any remaining work and releases resources.

## Benchmarks

Measured on an Apple M2 Pro (BenchmarkDotNet 0.14.0):

| Scenario | 32 items | 128 items |
| --- | --- | --- |
| Sequential (single lane) | 73.40 ms | 293.05 ms |
| Parallel lanes (single key) | 74.39 ms | 295.36 ms |
| Parallel lanes (mixed keys) | 18.86 ms | 74.38 ms |

- High contention adds ~1% overhead versus sequential execution.
- Mixed lanes deliver ~4x throughput improvement by overlapping independent work.
- Re-run locally with `dotnet run -c Release --project tests/ParallelLanes.Benchmarks/ParallelLanes.Benchmarks.csproj -- --filter "*"`.

## Repository Layout

- `src/ParallelLanes` – main library.
- `samples/ParallelLanes.SampleApp` – console demo showcasing DI and logging.
- `tests/ParallelLanes.Tests` – correctness suite with xUnit.
- `tests/ParallelLanes.Benchmarks` – BenchmarkDotNet scenarios.

## Roadmap

- Metrics hooks for queue depth, throughput, and idle workers.
- Named registrations for multi-tenant or multi-runner setups.
- Pluggable backpressure strategies and advanced scheduling policies.

## Contributing

Issues and pull requests are welcome. Please open an issue describing proposed changes before submitting large PRs.

## License

This project is licensed under the MIT License. See `LICENSE` for details.
