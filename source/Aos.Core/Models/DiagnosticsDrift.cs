namespace Aos.WebApi.Models;

// One-sided upper CUSUM: S[i] = max(0, S[i-1] + (x[i] - target - slack)). An alarm is the first index where S[i] > threshold.
public sealed record DriftSeries(
    string Name, string Unit, double Target, double Slack, double Threshold, IReadOnlyList<double> Values,
    IReadOnlyList<double> Statistics, int? AlarmIteration, bool Gating);
public sealed record LatencySummary(int Samples, double P50, double P95, double Max);
public sealed record DiagnosticsIterationResult(int Iteration, int Successes, int Cases, int StageFailures, double MedianPlannerMs, double MedianToolMs);

public static class DiagnosticsDrift
{
    public static DriftSeries Cusum(string name, string unit, IReadOnlyList<double> values, double target, double slack, double threshold, bool gating)
    {
        var statistics = new double[values.Count];
        int? alarm = null;
        var sum = 0.0;
        for (var i = 0; i < values.Count; i++)
        {
            sum = Math.Max(0, sum + values[i] - target - slack);
            statistics[i] = Math.Round(sum, 6);
            if (alarm is null && sum > threshold) alarm = i + 1;
        }
        return new(name, unit, target, slack, threshold, values, statistics, alarm, gating);
    }

    // Correctness drift: any stage failure in a pinned deterministic dataset is a signal, so a single failed iteration alarms.
    public static DriftSeries StageFailureDrift(IReadOnlyList<DiagnosticsIterationResult> iterations) =>
        Cusum("stage-failures", "count", iterations.Select(i => (double)i.StageFailures).ToArray(), 0, 0.25, 0.5, gating: true);

    // Latency drift is relative to the mean of the first `baselineIterations` iterations. Wall-clock latency is host-noisy,
    // so it is reported but does not gate the run.
    public static DriftSeries LatencyDrift(string name, IReadOnlyList<double> medians, int baselineIterations, double slackFraction, double thresholdFraction)
    {
        var window = Math.Clamp(baselineIterations, 1, Math.Max(1, medians.Count));
        var target = medians.Count == 0 ? 0 : medians.Take(window).Average();
        return Cusum(name, "ms", medians, target, target * slackFraction, target * thresholdFraction, gating: false);
    }

    // Nearest-rank percentile; deterministic and independent of input order.
    public static double Percentile(IEnumerable<double> values, double fraction)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) return 0;
        var rank = (int)Math.Ceiling(fraction * sorted.Length);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }

    public static LatencySummary Summarize(IReadOnlyCollection<double> values) =>
        new(values.Count, Math.Round(Percentile(values, 0.5), 4), Math.Round(Percentile(values, 0.95), 4), Math.Round(values.Count == 0 ? 0 : values.Max(), 4));
}
