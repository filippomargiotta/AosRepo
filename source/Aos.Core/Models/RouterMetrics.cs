namespace Aos.WebApi.Models;

public sealed record RouterModelMetricKey(
    string TaskClass,
    string Provider,
    string ModelId,
    string Version)
{
    public static RouterModelMetricKey FromCandidate(string taskClass, RouterModelCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return new RouterModelMetricKey(
            TaskClass: taskClass,
            Provider: candidate.Provider,
            ModelId: candidate.ModelId,
            Version: candidate.Version);
    }
}

public sealed record RouterModelPerformanceMetric(
    string TaskClass,
    string Provider,
    string ModelId,
    string Version,
    int ObservedLatencyMs,
    decimal SuccessRate,
    int QualityScore,
    int SampleCount,
    DateTimeOffset CapturedAtUtc,
    string Source)
{
    public RouterModelMetricKey Key => new(TaskClass, Provider, ModelId, Version);
}
