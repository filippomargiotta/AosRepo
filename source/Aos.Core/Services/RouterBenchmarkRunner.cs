using System.Diagnostics;
using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public sealed record RouterBenchmarkOptions(
    int Iterations,
    int WarmupIterations
);

public sealed record RouterBenchmarkReport(
    string TaskClass,
    int Iterations,
    int WarmupIterations,
    int RankedCandidateCount,
    int RejectionCount,
    string? SelectedProvider,
    string? SelectedModelId,
    string? SelectedVersion,
    decimal? SelectedScore,
    double MinLatencyMs,
    double MedianLatencyMs,
    double P95LatencyMs,
    double MaxLatencyMs
);

public static class RouterBenchmarkRunner
{
    public static RouterBenchmarkReport Run(
        IRouterService router,
        RouterSelectionRequest request,
        RouterBenchmarkOptions options)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(request);

        if (options.Iterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Iterations must be greater than zero.");
        }

        if (options.WarmupIterations < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Warmup iterations cannot be negative.");
        }

        RouterSelectionResult? lastResult = null;
        for (var i = 0; i < options.WarmupIterations; i++)
        {
            lastResult = router.SelectModel(request);
        }

        var elapsedTicks = new long[options.Iterations];
        for (var i = 0; i < options.Iterations; i++)
        {
            var startedAt = Stopwatch.GetTimestamp();
            lastResult = router.SelectModel(request);
            elapsedTicks[i] = Stopwatch.GetTimestamp() - startedAt;
        }

        Array.Sort(elapsedTicks);
        lastResult ??= router.SelectModel(request);

        return new RouterBenchmarkReport(
            TaskClass: lastResult.TaskClass,
            Iterations: options.Iterations,
            WarmupIterations: options.WarmupIterations,
            RankedCandidateCount: lastResult.RankedCandidates.Count,
            RejectionCount: lastResult.RejectionReasons.Count,
            SelectedProvider: lastResult.SelectedCandidate?.Provider,
            SelectedModelId: lastResult.SelectedCandidate?.ModelId,
            SelectedVersion: lastResult.SelectedCandidate?.Version,
            SelectedScore: lastResult.RankedCandidates.FirstOrDefault()?.Score,
            MinLatencyMs: ToMilliseconds(elapsedTicks[0]),
            MedianLatencyMs: ToMilliseconds(elapsedTicks[PercentileIndex(elapsedTicks.Length, 0.50)]),
            P95LatencyMs: ToMilliseconds(elapsedTicks[PercentileIndex(elapsedTicks.Length, 0.95)]),
            MaxLatencyMs: ToMilliseconds(elapsedTicks[^1]));
    }

    private static int PercentileIndex(int length, double percentile)
    {
        var index = (int)Math.Ceiling(length * percentile) - 1;
        return Math.Clamp(index, 0, length - 1);
    }

    private static double ToMilliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
