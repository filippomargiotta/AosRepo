namespace Aos.WebApi.Models;

public static class PlannerSchemaVersions
{
    public const string CurrentPlanVersion = "0.1";
    public const string CurrentPlaybookVersion = "0.1";

    public static bool IsSupportedPlanVersion(string schemaVersion) =>
        string.Equals(schemaVersion, CurrentPlanVersion, StringComparison.Ordinal);

    public static bool IsSupportedPlaybookVersion(string schemaVersion) =>
        string.Equals(schemaVersion, CurrentPlaybookVersion, StringComparison.Ordinal);
}

public sealed record PlannerPlan(
    string SchemaVersion,
    string PlanId,
    string TaskClass,
    IReadOnlyList<PlannerPlanStep> Steps
);

public sealed record PlannerPlanStep(
    int Sequence,
    string StepId,
    string ActionId,
    IReadOnlyDictionary<string, string> Arguments
);

public sealed record AllowedActionDefinition(
    string ActionId,
    ToolRef Tool,
    IReadOnlyList<AllowedActionArgumentDefinition> Arguments
);

public sealed record AllowedActionArgumentDefinition(
    string Name,
    bool Required
);

public sealed record PlannerPlaybook(
    string SchemaVersion,
    string PlaybookId,
    string Version,
    string TaskClass,
    string Description,
    IReadOnlyList<string> RetrievalTerms,
    IReadOnlyList<PlannerPlaybookStep> Steps
);

public sealed record PlannerPlaybookStep(
    string ActionId,
    IReadOnlyDictionary<string, string> ArgumentTemplates
);

public sealed record PlannerValidationError(
    string Code,
    string Path,
    string Message
);

public sealed record PlannerValidationResult(
    bool IsValid,
    IReadOnlyList<PlannerValidationError> Errors
);

public sealed record PlaybookRetrievalRequest(
    string TaskClass,
    IReadOnlyList<string> Terms,
    int MaxResults = 5
);

public sealed record PlaybookMatch(
    PlannerPlaybook Playbook,
    int Score,
    IReadOnlyList<string> MatchedTerms
);
