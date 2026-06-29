using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public sealed record SandboxBenchmarkOptions(
    int Iterations,
    int WarmupIterations
);

public sealed record SandboxBenchmarkReport(
    int Iterations,
    int WarmupIterations,
    int PoolSize,
    string ExecutorType,
    int WarmStartCount,
    int ColdStartCount,
    double WarmAcquireMinMs,
    double WarmAcquireMedianMs,
    double WarmAcquireP95Ms,
    double WarmAcquireMaxMs,
    double ColdAcquireMinMs,
    double ColdAcquireMedianMs,
    double ColdAcquireP95Ms,
    double ColdAcquireMaxMs,
    double TotalLatencyMinMs,
    double TotalLatencyMedianMs,
    double TotalLatencyP95Ms,
    double TotalLatencyMaxMs
);

public static class SandboxBenchmarkRunner
{
    private static readonly ToolRef BenchmarkTool = new("benchmark-echo", "1.0");

    public static SandboxBenchmarkReport Run(
        PooledSandboxToolExecutor executor,
        int poolSize,
        string executorType,
        SandboxBenchmarkOptions options)
    {
        ArgumentNullException.ThrowIfNull(executor);

        if (options.Iterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Iterations must be greater than zero.");
        }

        if (options.WarmupIterations < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Warmup iterations cannot be negative.");
        }

        var request = new ToolExecutionRequest(
            RunId: "benchmark-run",
            InvocationId: "benchmark-invocation",
            Tool: BenchmarkTool,
            Action: "tool.execute",
            InputJson: "{\"benchmark\":true}",
            CapabilityToken: string.Empty,
            RequestedAtUtc: DateTimeOffset.UtcNow);

        for (var i = 0; i < options.WarmupIterations; i++)
        {
            executor.Execute(request);
        }

        var warmAcquireTicks = new List<double>(options.Iterations);
        var coldAcquireTicks = new List<double>(options.Iterations);
        var totalTicks = new double[options.Iterations];

        for (var i = 0; i < options.Iterations; i++)
        {
            var result = executor.Execute(request);
            var info = result.SandboxExecution!;
            var total = info.AcquireLatencyMs + info.ExecutionLatencyMs;

            if (info.WarmStart)
            {
                warmAcquireTicks.Add(info.AcquireLatencyMs);
            }
            else
            {
                coldAcquireTicks.Add(info.AcquireLatencyMs);
            }

            totalTicks[i] = total;
        }

        Array.Sort(totalTicks);
        warmAcquireTicks.Sort();
        coldAcquireTicks.Sort();

        return new SandboxBenchmarkReport(
            Iterations: options.Iterations,
            WarmupIterations: options.WarmupIterations,
            PoolSize: poolSize,
            ExecutorType: executorType,
            WarmStartCount: warmAcquireTicks.Count,
            ColdStartCount: coldAcquireTicks.Count,
            WarmAcquireMinMs: warmAcquireTicks.Count > 0 ? warmAcquireTicks[0] : 0,
            WarmAcquireMedianMs: warmAcquireTicks.Count > 0 ? warmAcquireTicks[PercentileIndex(warmAcquireTicks.Count, 0.50)] : 0,
            WarmAcquireP95Ms: warmAcquireTicks.Count > 0 ? warmAcquireTicks[PercentileIndex(warmAcquireTicks.Count, 0.95)] : 0,
            WarmAcquireMaxMs: warmAcquireTicks.Count > 0 ? warmAcquireTicks[^1] : 0,
            ColdAcquireMinMs: coldAcquireTicks.Count > 0 ? coldAcquireTicks[0] : 0,
            ColdAcquireMedianMs: coldAcquireTicks.Count > 0 ? coldAcquireTicks[PercentileIndex(coldAcquireTicks.Count, 0.50)] : 0,
            ColdAcquireP95Ms: coldAcquireTicks.Count > 0 ? coldAcquireTicks[PercentileIndex(coldAcquireTicks.Count, 0.95)] : 0,
            ColdAcquireMaxMs: coldAcquireTicks.Count > 0 ? coldAcquireTicks[^1] : 0,
            TotalLatencyMinMs: totalTicks[0],
            TotalLatencyMedianMs: totalTicks[PercentileIndex(totalTicks.Length, 0.50)],
            TotalLatencyP95Ms: totalTicks[PercentileIndex(totalTicks.Length, 0.95)],
            TotalLatencyMaxMs: totalTicks[^1]);
    }

    private static int PercentileIndex(int length, double percentile)
    {
        var index = (int)Math.Ceiling(length * percentile) - 1;
        return Math.Clamp(index, 0, length - 1);
    }
}
