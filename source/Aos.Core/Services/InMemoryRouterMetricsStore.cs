using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Microsoft.Extensions.Options;

namespace Aos.WebApi.Services;

public sealed class InMemoryRouterMetricsStore : IRouterMetricsStore
{
    private readonly IReadOnlyDictionary<RouterModelMetricKey, RouterModelPerformanceMetric> _metricsByKey;

    public InMemoryRouterMetricsStore(IOptions<RouterMetricsOptions> options)
        : this(options.Value.Metrics.Select(MapMetric))
    {
    }

    public InMemoryRouterMetricsStore(IEnumerable<RouterModelPerformanceMetric> metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        var metricsByKey = new Dictionary<RouterModelMetricKey, RouterModelPerformanceMetric>();
        foreach (var metric in metrics)
        {
            ValidateMetric(metric);
            if (!metricsByKey.TryAdd(metric.Key, metric))
            {
                throw new InvalidOperationException(
                    $"RouterMetrics contains duplicate metric entry for {FormatKey(metric.Key)}.");
            }
        }

        _metricsByKey = metricsByKey;
    }

    public bool TryGetMetric(
        string taskClass,
        RouterModelCandidate candidate,
        out RouterModelPerformanceMetric? metric)
    {
        if (string.IsNullOrWhiteSpace(taskClass))
        {
            throw new ArgumentException("Task class is required.", nameof(taskClass));
        }

        ArgumentNullException.ThrowIfNull(candidate);

        return _metricsByKey.TryGetValue(
            RouterModelMetricKey.FromCandidate(taskClass, candidate),
            out metric);
    }

    public IReadOnlyList<RouterModelPerformanceMetric> ListMetrics(string taskClass)
    {
        if (string.IsNullOrWhiteSpace(taskClass))
        {
            throw new ArgumentException("Task class is required.", nameof(taskClass));
        }

        return _metricsByKey.Values
            .Where(metric => string.Equals(metric.TaskClass, taskClass, StringComparison.Ordinal))
            .OrderBy(metric => metric.Provider, StringComparer.Ordinal)
            .ThenBy(metric => metric.ModelId, StringComparer.Ordinal)
            .ThenBy(metric => metric.Version, StringComparer.Ordinal)
            .ToArray();
    }

    private static RouterModelPerformanceMetric MapMetric(RouterModelMetricOptions metric)
    {
        return new RouterModelPerformanceMetric(
            TaskClass: metric.TaskClass,
            Provider: metric.Provider,
            ModelId: metric.ModelId,
            Version: metric.Version,
            ObservedLatencyMs: metric.ObservedLatencyMs,
            SuccessRate: metric.SuccessRate,
            QualityScore: metric.QualityScore,
            SampleCount: metric.SampleCount,
            CapturedAtUtc: metric.CapturedAtUtc,
            Source: metric.Source);
    }

    private static void ValidateMetric(RouterModelPerformanceMetric metric)
    {
        if (string.IsNullOrWhiteSpace(metric.TaskClass))
        {
            throw new InvalidOperationException("RouterMetrics.Metrics[].TaskClass is required.");
        }

        if (string.IsNullOrWhiteSpace(metric.Provider))
        {
            throw new InvalidOperationException("RouterMetrics.Metrics[].Provider is required.");
        }

        if (string.IsNullOrWhiteSpace(metric.ModelId))
        {
            throw new InvalidOperationException("RouterMetrics.Metrics[].ModelId is required.");
        }

        if (string.IsNullOrWhiteSpace(metric.Version))
        {
            throw new InvalidOperationException("RouterMetrics.Metrics[].Version is required.");
        }

        if (metric.ObservedLatencyMs < 0)
        {
            throw new InvalidOperationException("RouterMetrics.Metrics[].ObservedLatencyMs cannot be negative.");
        }

        if (metric.SuccessRate is < 0m or > 1m)
        {
            throw new InvalidOperationException("RouterMetrics.Metrics[].SuccessRate must be between 0 and 1.");
        }

        if (metric.QualityScore is < 0 or > 100)
        {
            throw new InvalidOperationException("RouterMetrics.Metrics[].QualityScore must be between 0 and 100.");
        }

        if (metric.SampleCount < 0)
        {
            throw new InvalidOperationException("RouterMetrics.Metrics[].SampleCount cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(metric.Source))
        {
            throw new InvalidOperationException("RouterMetrics.Metrics[].Source is required.");
        }
    }

    private static string FormatKey(RouterModelMetricKey key) =>
        $"{key.TaskClass}/{key.Provider}/{key.ModelId}/{key.Version}";
}
