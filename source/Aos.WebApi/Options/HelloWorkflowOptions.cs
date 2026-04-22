namespace Aos.WebApi.Options;

public sealed class HelloWorkflowOptions
{
    public const string SectionName = "HelloWorkflow";

    public HelloWorkflowRoutingOptions Routing { get; set; } = new();
    public List<HelloWorkflowModelOptions> Models { get; set; } = [];
    public List<HelloWorkflowToolOptions> Tools { get; set; } = [];
    public List<HelloWorkflowPolicyOptions> PolicyDecisions { get; set; } = [];
}

public sealed class HelloWorkflowRoutingOptions
{
    public string TaskClass { get; set; } = "workflow.hello";

    public int? MaxLatencyMs { get; set; } = 220;

    public decimal? MaxCostPer1KTokens { get; set; } = 0.5m;

    public int? MinQualityScore { get; set; } = 60;

    public List<string> RequiredComplianceTags { get; set; } = [ "eu", "standard" ];
}

public sealed class HelloWorkflowModelOptions
{
    public string ModelId { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
}

public sealed class HelloWorkflowToolOptions
{
    public string ToolId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
}

public sealed class HelloWorkflowPolicyOptions
{
    public string PolicyId { get; set; } = string.Empty;
    public string Decision { get; set; } = string.Empty;
    public string? Reason { get; set; }
}
