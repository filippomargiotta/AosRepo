namespace Aos.WebApi.Models;

public sealed record DiagnosticsEvaluationCase(
    string CaseId, DiagnosticsRequest Request, string ExpectedStatus, string ExpectedReason,
    IReadOnlyList<string> RequiredEvidencePaths, string? RequiredTestProfile, bool Recoverable, int ExpectedAttempts);
public sealed record DiagnosticsCaseMetrics(
    string CaseId, bool TaskSuccess, double ValidPlanRate, bool EvidenceGroundingPassed,
    double ExecutedStepSuccessRate, IReadOnlyList<string> CriticRejectionReasons,
    int RecoveryAttempts, bool RecoverySucceeded, bool? ProhibitedActionRejected,
    bool ReplayEquivalent, double PlannerOverheadMs, double ToolDurationMs, double SandboxOverheadMs, string Outcome, string ReasonCode);
public sealed record DiagnosticsEvaluationReport(
    string SchemaVersion, string Executor, RepositorySnapshotRef Snapshot,
    IReadOnlyList<DiagnosticsCaseMetrics> PlannerV1, IReadOnlyList<DiagnosticsCaseMetrics> PlannerV2,
    int V1Successes, int V2Successes, int RecoverableSuccessImprovement, bool NonRecoveryRegressed, bool Passed);

public static class DiagnosticsCaseScorer
{
    public static bool Score(DiagnosticsEvaluationCase expected, DiagnosticsOutcome outcome,
        IReadOnlyList<ToolExecutionEvent> tools, bool v2)
    {
        if (outcome.Status != expected.ExpectedStatus || outcome.ReasonCode != expected.ExpectedReason ||
            (v2 && outcome.Attempts != expected.ExpectedAttempts)) return false;
        if (outcome.Status == "succeeded")
        {
            if (string.IsNullOrWhiteSpace(outcome.Diagnosis) || outcome.Evidence.Count == 0) return false;
            if (expected.RequiredEvidencePaths.Any(path => !outcome.Evidence.Any(e => e.Path == path))) return false;
            if (expected.RequiredTestProfile is not null && !outcome.Evidence.Any(e => e.Kind == "test" && tools.Any(t =>
                t.InvocationId == e.InvocationId && t.Status == "succeeded" &&
                System.Text.Json.JsonSerializer.Deserialize<RepositoryToolOutput>(t.OutputJson, Services.RepositoryToolExecutor.JsonOptions)?.ProfileId == expected.RequiredTestProfile)))
                return false;
        }
        return true;
    }
}
