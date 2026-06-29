using Aos.WebApi.Options;
using Aos.WebApi.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aos.WebApi.Tests;

public class SmokeTests
{
    // Verifies that the factory-based DI registrations for the router metrics path are
    // unambiguous and resolve to the correct types. Guards against the multi-constructor
    // InMemoryRouterMetricsStore ambiguity that would cause a runtime exception if registered
    // via AddSingleton<T, TImpl> (both constructors would be satisfiable by the DI container).
    [Fact]
    public void Di_RouterService_ResolvesWithMetricsStoreFromOptions()
    {
        var services = new ServiceCollection();

        var routerOptions = new RouterOptions
        {
            Weights = new RouterWeightsOptions { Latency = 0.35m, Cost = 0.2m, Quality = 0.3m, Compliance = 0.15m },
            Candidates =
            [
                new RouterModelOptions
                {
                    ModelId = "test-model",
                    Provider = "test-provider",
                    Version = "1.0",
                    LatencyMs = 100,
                    CostPer1KTokens = 0.1m,
                    QualityScore = 80,
                    ComplianceScore = 90,
                    ComplianceTags = ["standard"]
                }
            ],
            Policies = []
        };

        var metricsOptions = new RouterMetricsOptions
        {
            Enabled = true,
            BlendWeight = 0.5m,
            Metrics =
            [
                new RouterModelMetricOptions
                {
                    TaskClass = "test",
                    Provider = "test-provider",
                    ModelId = "test-model",
                    Version = "1.0",
                    ObservedLatencyMs = 95,
                    SuccessRate = 0.99m,
                    QualityScore = 82,
                    SampleCount = 100,
                    CapturedAtUtc = DateTimeOffset.UtcNow,
                    Source = "smoke-test"
                }
            ]
        };

        services.AddSingleton<IOptions<RouterOptions>>(
            Microsoft.Extensions.Options.Options.Create(routerOptions));
        services.AddSingleton<IOptions<RouterMetricsOptions>>(
            Microsoft.Extensions.Options.Options.Create(metricsOptions));

        // Factory registration — same pattern as Program.cs — resolves the options-accepting
        // constructor explicitly, avoiding the ambiguity with the IEnumerable<T> constructor.
        services.AddSingleton<IRouterMetricsStore>(sp =>
            new InMemoryRouterMetricsStore(
                sp.GetRequiredService<IOptions<RouterMetricsOptions>>()));
        services.AddSingleton<IRouterService>(sp =>
            new DeterministicRouterService(
                sp.GetRequiredService<IOptions<RouterOptions>>(),
                sp.GetRequiredService<IOptions<RouterMetricsOptions>>(),
                sp.GetRequiredService<IRouterMetricsStore>()));

        using var provider = services.BuildServiceProvider();

        var router = provider.GetRequiredService<IRouterService>();
        var metricsStore = provider.GetRequiredService<IRouterMetricsStore>();

        Assert.IsType<DeterministicRouterService>(router);
        Assert.IsType<InMemoryRouterMetricsStore>(metricsStore);

        // Verify the metrics store was populated from options, not constructed via the
        // IEnumerable<RouterModelPerformanceMetric> constructor (which would give an empty store).
        var metrics = metricsStore.ListMetrics("test");
        Assert.Single(metrics);
        Assert.Equal("test-model", metrics[0].ModelId);
    }

    [Fact]
    public void Di_SandboxPool_ResolvesFromOptions()
    {
        var services = new ServiceCollection();

        var poolOptions = new SandboxPoolOptions { PoolSize = 2, ExecutorType = "in-process-v1" };
        services.AddSingleton<IOptions<SandboxPoolOptions>>(
            Microsoft.Extensions.Options.Options.Create(poolOptions));
        services.AddSingleton<PreWarmedSandboxPool>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<SandboxPoolOptions>>().Value;
            return new PreWarmedSandboxPool(opts.PoolSize);
        });
        services.AddSingleton<PooledSandboxToolExecutor>();

        using var provider = services.BuildServiceProvider();

        var pool = provider.GetRequiredService<PreWarmedSandboxPool>();
        var executor = provider.GetRequiredService<PooledSandboxToolExecutor>();

        Assert.IsType<PreWarmedSandboxPool>(pool);
        Assert.IsType<PooledSandboxToolExecutor>(executor);
        Assert.Equal(2, pool.CurrentPoolSize);
    }
}
