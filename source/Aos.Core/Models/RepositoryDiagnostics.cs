namespace Aos.WebApi.Models;

public static class DiagnosticsSchema
{
    public const string Version = "0.1";
    public const string Workflow = "diagnostics";
}

public sealed record RepositorySnapshotRef(
    string SchemaVersion, string SnapshotId, string ContentHash, string SourceRevision, DateTimeOffset CreatedAtUtc);

public sealed record RepositoryLine(string Path, int Line, string Text);
public sealed record RepositoryToolOutput(
    RepositorySnapshotRef Snapshot, string Kind, IReadOnlyList<RepositoryLine> Lines,
    bool Truncated = false, string? ProfileId = null, int? ExitCode = null,
    string? Stdout = null, string? Stderr = null, string? FailureClassification = null);

public sealed record DiagnosticEvidenceRef(
    string InvocationId, string Kind, string? Path, int? Line, string ContentHash);
public sealed record DiagnosticEvidenceRequirement(string Kind, string? Path, string Contains);
public sealed record DiagnosticPlaybookPolicy(
    string PlaybookId, string Version, string Diagnosis,
    IReadOnlyList<DiagnosticEvidenceRequirement> Requirements);

public sealed record DiagnosticsRequest(RepositorySnapshotRef Snapshot, PlannerTaskRequest Task);
public sealed record DiagnosticsRecoverySettings(
    int MaxRetries, IReadOnlyList<string> AlternateTerms, IReadOnlyDictionary<string, string> NarrowerProfiles);
public sealed record DiagnosticsStartedEvent(
    string SchemaVersion, string ManifestVersion, DiagnosticsRequest Request,
    DiagnosticsRecoverySettings Recovery, IReadOnlyList<AllowedActionDefinition> AllowedActions);
public sealed record DiagnosticsPlanEvent(
    int Attempt, PlannerTaskRequest Task, PlannerPlan? Plan,
    PlannerPlaybookSelection? Selection, DiagnosticPlaybookPolicy? Policy,
    string CandidateSource, bool IsValid, string? ErrorCode);
public sealed record DiagnosticsCriticInput(
    RepositorySnapshotRef Snapshot, DiagnosticsPlanEvent Planning, IReadOnlyList<PlannerStepExecutionResult> Steps);
public sealed record DiagnosticsCriticDecision(
    int Attempt, string Decision, string ReasonCode,
    IReadOnlyList<DiagnosticEvidenceRef> Evidence, string? Diagnosis, string Source = "rules-v1");
public sealed record DiagnosticsRecoveryDecision(
    int Attempt, string PriorFailure, string Strategy, PlannerTaskRequest? NextTask, string? TerminalReason);
public sealed record DiagnosticsOutcome(
    string Status, string ReasonCode, int Attempts, string? Diagnosis, IReadOnlyList<DiagnosticEvidenceRef> Evidence);
public sealed record DiagnosticsWorkflowArtifacts(
    ManifestRecord ManifestRecord, IReadOnlyList<EventLogRecord> EventLogRecords, DiagnosticsOutcome Outcome,
    [property: System.Text.Json.Serialization.JsonIgnore] DiagnosticsTiming? Timing = null);
public sealed record DiagnosticsTiming(double PlannerOverheadMs, double ToolDurationMs, double SandboxOverheadMs);
