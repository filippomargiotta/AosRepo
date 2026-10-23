namespace Aos.WebApi.Models;

public sealed record DiagnosticsEvaluationCase(
    string CaseId, DiagnosticsRequest Request, string ExpectedStatus, string ExpectedReason,
    IReadOnlyList<string> RequiredEvidencePaths, string? RequiredTestProfile, bool Recoverable, int ExpectedAttempts,
    string? ExpectedDenialCode = null, bool ReplayWithoutSnapshot = false, DiagnosticsScoringSpec? Scoring = null);
// Operator-authored semantic expectations. Each concept group is a set of accepted phrasings of one required fact.
public sealed record DiagnosticsEvidenceFact(string Path, string Contains);
public sealed record DiagnosticsScoringSpec(
    string? ExpectedDiagnosis, IReadOnlyList<IReadOnlyList<string>> Concepts, IReadOnlyList<string> ForbiddenConcepts,
    IReadOnlyList<DiagnosticsEvidenceFact> EvidenceFacts, IReadOnlyList<string>? AllowedReadPaths);
public sealed record DiagnosticsScore(bool Passed, double ExactScore, double SemanticScore, IReadOnlyList<string> Failures);
// Stage results attribute a case outcome to planning, tool execution, evidence, recovery, or replay.
public sealed record DiagnosticsStageResults(bool Planning, bool Tool, bool Evidence, bool Recovery, bool Replay);
public sealed record DiagnosticsToolCallRow(int Index, string ToolId, string Status, string? Error);
public sealed record DiagnosticsCaseMetrics(
    string CaseId, bool TaskSuccess, double ValidPlanRate, bool EvidenceGroundingPassed,
    double ExecutedStepSuccessRate, IReadOnlyList<string> CriticRejectionReasons,
    int RecoveryAttempts, bool RecoverySucceeded, bool? ProhibitedActionRejected,
    bool ReplayEquivalent, double PlannerOverheadMs, double ToolDurationMs, double SandboxOverheadMs, string Outcome, string ReasonCode,
    bool? SnapshotRemovedBeforeReplay = null, double ExactScore = 1, double SemanticScore = 1,
    IReadOnlyList<string>? ScoreFailures = null, bool AllowedActionsValid = true,
    DiagnosticsStageResults? Stages = null, IReadOnlyList<DiagnosticsToolCallRow>? ToolCalls = null);
public sealed record DiagnosticsEvaluationReport(
    string SchemaVersion, string Executor, RepositorySnapshotRef Snapshot,
    IReadOnlyList<DiagnosticsCaseMetrics> PlannerV1, IReadOnlyList<DiagnosticsCaseMetrics> PlannerV2,
    int V1Successes, int V2Successes, int RecoverableSuccessImprovement, bool NonRecoveryRegressed, bool Passed,
    IReadOnlyList<RepositorySnapshotRef>? Snapshots = null, DiagnosticsGateThresholds? Thresholds = null,
    IReadOnlyList<DiagnosticsGateCheck>? GateChecks = null, DiagnosticsStageSummary? StageSummary = null,
    IReadOnlyList<string>? Regressions = null);
public sealed record DiagnosticsGateThresholds(int MinCases, int MinSuccesses);
public sealed record DiagnosticsGateCheck(string Name, bool Passed, string Detail);
public sealed record DiagnosticsStageCount(int Passed, int Failed, IReadOnlyList<string> FailedCases);
public sealed record DiagnosticsStageSummary(
    IReadOnlyDictionary<string, DiagnosticsStageCount> PlannerV1, IReadOnlyDictionary<string, DiagnosticsStageCount> PlannerV2);

public static class DiagnosticsCaseScorer
{
    public static bool Score(DiagnosticsEvaluationCase expected, DiagnosticsOutcome outcome,
        IReadOnlyList<ToolExecutionEvent> tools, bool v2) => Evaluate(expected, outcome, tools, v2).Passed;

    // Structural checks and semantic checks gate a case. Exact diagnosis wording is scored but never gates,
    // so harmless rewording passes while an unsupported or contradictory diagnosis fails.
    public static DiagnosticsScore Evaluate(DiagnosticsEvaluationCase expected, DiagnosticsOutcome outcome,
        IReadOnlyList<ToolExecutionEvent> tools, bool v2)
    {
        var failures = new List<string>();
        var structural = new List<bool>();
        var semantic = new List<bool>();
        bool Check(List<bool> bucket, bool ok, string failure)
        {
            bucket.Add(ok);
            if (!ok) failures.Add(failure);
            return ok;
        }
        Check(structural, outcome.Status == expected.ExpectedStatus, $"status:{outcome.Status}");
        Check(structural, outcome.ReasonCode == expected.ExpectedReason, $"reason:{outcome.ReasonCode}");
        if (v2) Check(structural, outcome.Attempts == expected.ExpectedAttempts, $"attempts:{outcome.Attempts}");
        var succeeded = outcome.Status == "succeeded";
        if (expected.ExpectedStatus == "succeeded" && succeeded)
        {
            Check(structural, !string.IsNullOrWhiteSpace(outcome.Diagnosis) && outcome.Evidence.Count > 0, "diagnosis.missing");
            foreach (var path in expected.RequiredEvidencePaths)
                Check(structural, outcome.Evidence.Any(e => e.Path == path), $"evidence.path:{path}");
            if (expected.RequiredTestProfile is not null)
                Check(structural, outcome.Evidence.Any(e => e.Kind == "test" && tools.Any(t =>
                    t.InvocationId == e.InvocationId && t.Status == "succeeded" && ProfileOf(t) == expected.RequiredTestProfile)), "evidence.test");
        }
        else if (expected.ExpectedStatus != "succeeded")
            Check(semantic, outcome.Diagnosis is null && outcome.Evidence.Count == 0, "diagnosis.unexpected");
        var spec = expected.Scoring;
        var exactDiagnosis = true;
        if (spec is not null)
        {
            if (succeeded && expected.ExpectedStatus == "succeeded")
            {
                var diagnosis = Normalize(outcome.Diagnosis ?? "");
                foreach (var group in spec.Concepts)
                    Check(semantic, group.Any(c => Contains(diagnosis, c)), $"concept:{group.FirstOrDefault()}");
                foreach (var forbidden in spec.ForbiddenConcepts)
                    Check(semantic, !Contains(diagnosis, forbidden), $"forbidden:{forbidden}");
                foreach (var fact in spec.EvidenceFacts)
                    Check(semantic, outcome.Evidence.Any(e => e.Kind == "read" && e.Path == fact.Path && e.Line is not null &&
                        LineText(tools, e)?.Contains(fact.Contains, StringComparison.Ordinal) == true), $"fact:{fact.Path}");
                exactDiagnosis = spec.ExpectedDiagnosis is null || outcome.Diagnosis == spec.ExpectedDiagnosis;
            }
            if (spec.AllowedReadPaths is not null)
                foreach (var path in tools.Where(t => t.ToolId == "repo.read" && t.Status == "succeeded").Select(ReadPathOf).Distinct(StringComparer.Ordinal))
                    Check(semantic, path is not null && spec.AllowedReadPaths.Contains(path), $"scope:{path}");
        }
        var exactChecks = structural.Append(exactDiagnosis).ToArray();
        return new(structural.All(x => x) && semantic.All(x => x), Ratio(exactChecks), Ratio(semantic), failures.AsReadOnly());
    }

    // Lowercase alphanumeric tokens with ".0" suffixes dropped, so case, spacing, punctuation, and 20 vs 20.0 do not matter.
    public static string Normalize(string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text.ToLowerInvariant(), @"(?<=\d)\.0+(?!\d)", "");
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var c in text) builder.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool Contains(string normalizedText, string phrase)
    {
        var needle = Normalize(phrase);
        return needle.Length > 0 && $" {normalizedText} ".Contains($" {needle} ", StringComparison.Ordinal);
    }

    private static double Ratio(IReadOnlyCollection<bool> checks) => checks.Count == 0 ? 1 : (double)checks.Count(x => x) / checks.Count;

    private static string? ProfileOf(ToolExecutionEvent tool) =>
        System.Text.Json.JsonSerializer.Deserialize<RepositoryToolOutput>(tool.OutputJson, Services.RepositoryToolExecutor.JsonOptions)?.ProfileId;

    private static string? ReadPathOf(ToolExecutionEvent tool)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(tool.InputJson)?.GetValueOrDefault("path"); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static string? LineText(IReadOnlyList<ToolExecutionEvent> tools, DiagnosticEvidenceRef evidence)
    {
        var tool = tools.FirstOrDefault(t => t.InvocationId == evidence.InvocationId && t.Status == "succeeded");
        if (tool is null) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<RepositoryToolOutput>(tool.OutputJson, Services.RepositoryToolExecutor.JsonOptions)?
                .Lines.FirstOrDefault(l => l.Path == evidence.Path && l.Line == evidence.Line)?.Text;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
