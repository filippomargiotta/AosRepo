namespace Aos.WebApi.Models;

public sealed record RouterSelectionRequest(
    string TaskClass,
    int? MaxLatencyMs,
    decimal? MaxCostPer1KTokens,
    int? MinQualityScore,
    IReadOnlyList<string>? RequiredComplianceTags
);

public sealed record RouterModelCandidate(
    string ModelId,
    string Provider,
    string Version,
    int LatencyMs,
    decimal CostPer1KTokens,
    int QualityScore,
    int ComplianceScore,
    IReadOnlyList<string> ComplianceTags
);

public sealed record RouterCandidateScore(
    RouterModelCandidate Candidate,
    decimal Score
);

public sealed record RouterSelectionResult(
    string TaskClass,
    RouterModelCandidate? SelectedCandidate,
    IReadOnlyList<RouterCandidateScore> RankedCandidates,
    IReadOnlyList<string> RejectionReasons
);
