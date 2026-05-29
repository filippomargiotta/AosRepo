namespace Aos.WebApi.Options;

public sealed class RouterMetricsOptions
{
    public const string SectionName = "RouterMetrics";

    public bool Enabled { get; set; }

    public decimal BlendWeight { get; set; } = 0.5m;

    public List<RouterModelMetricOptions> Metrics { get; set; } = [];
}

public sealed class RouterModelMetricOptions
{
    public string TaskClass { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string ModelId { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public int ObservedLatencyMs { get; set; }

    public decimal SuccessRate { get; set; }

    public int QualityScore { get; set; }

    public int SampleCount { get; set; }

    public DateTimeOffset CapturedAtUtc { get; set; }

    public string Source { get; set; } = string.Empty;
}
