using Microsoft.Extensions.DependencyInjection;

namespace ParallelLanes;

/// <summary>
///     Registration helpers for dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Registers <see cref="IParallelLaneRunner" /> and optionally configures its options.
    /// </summary>
    public static IServiceCollection AddParallelLaneRunner(this IServiceCollection services,
        Action<ParallelLaneRunnerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<ParallelLaneRunnerOptions>();
        if (configure is not null) optionsBuilder.Configure(configure);

        services.AddSingleton<IParallelLaneRunner, ParallelLaneRunner>();
        services.AddSingleton(sp => (ParallelLaneRunner)sp.GetRequiredService<IParallelLaneRunner>());

        return services;
    }
}