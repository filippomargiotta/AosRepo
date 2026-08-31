namespace Aos.WebApi.Options;

public sealed class PlannerWorkflowOptions
{
    public const string SectionName = "PlannerWorkflow";

    public PlannerRoutingOptions Routing { get; set; } = new();
    public List<PlannerAllowedActionOptions> AllowedActions { get; set; } = [];
    public List<PlannerPlaybookOptions> Playbooks { get; set; } = [];
    public List<HelloWorkflowPolicyOptions> PolicyDecisions { get; set; } = [];
}

public sealed class PlannerRoutingOptions
{
    public int? MaxLatencyMs { get; set; } = 220;
    public decimal? MaxCostPer1KTokens { get; set; } = 0.5m;
    public int? MinQualityScore { get; set; } = 60;
    public List<string> RequiredComplianceTags { get; set; } = ["eu", "standard"];
}

public sealed class PlannerAllowedActionOptions
{
    public string ActionId { get; set; } = string.Empty;
    public string ToolId { get; set; } = string.Empty;
    public string ToolVersion { get; set; } = string.Empty;
    public List<PlannerAllowedActionArgumentOptions> Arguments { get; set; } = [];
}

public sealed class PlannerAllowedActionArgumentOptions
{
    public string Name { get; set; } = string.Empty;
    public bool Required { get; set; }
}

public sealed class PlannerPlaybookOptions
{
    public string SchemaVersion { get; set; } = "0.1";
    public string PlaybookId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string TaskClass { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> RetrievalTerms { get; set; } = [];
    public List<PlannerPlaybookStepOptions> Steps { get; set; } = [];
}

public sealed class PlannerPlaybookStepOptions
{
    public string ActionId { get; set; } = string.Empty;
    public Dictionary<string, string> ArgumentTemplates { get; set; } = new(StringComparer.Ordinal);
}
