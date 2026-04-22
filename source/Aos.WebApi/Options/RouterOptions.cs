namespace Aos.WebApi.Options;

public sealed class RouterOptions
{
    public const string SectionName = "Router";

    public RouterWeightsOptions Weights { get; set; } = new();

    public List<RouterModelOptions> Candidates { get; set; } = [];

    public List<RouterPolicyOptions> Policies { get; set; } = [];
}

public sealed class RouterWeightsOptions
{
    public decimal Latency { get; set; } = 0.35m;

    public decimal Cost { get; set; } = 0.20m;

    public decimal Quality { get; set; } = 0.30m;

    public decimal Compliance { get; set; } = 0.15m;
}

public sealed class RouterModelOptions
{
    public string ModelId { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public int LatencyMs { get; set; }

    public decimal CostPer1KTokens { get; set; }

    public int QualityScore { get; set; }

    public int ComplianceScore { get; set; }

    public List<string> ComplianceTags { get; set; } = [];
}

public sealed class RouterPolicyOptions
{
    public string PolicyId { get; set; } = string.Empty;

    public string TaskClass { get; set; } = string.Empty;

    public int? MaxLatencyMs { get; set; }

    public decimal? MaxCostPer1KTokens { get; set; }

    public int? MinQualityScore { get; set; }

    public List<string> RequiredComplianceTags { get; set; } = [];

    public RouterWeightsOptions? Weights { get; set; }
}
