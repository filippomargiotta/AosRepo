using Aos.WebApi.Models;

namespace Aos.WebApi.Tests;

internal static class RouterTestData
{
    public static RouterModelCandidate CreateCandidate(
        string modelId = "model-1",
        string provider = "local",
        string version = "0.0",
        int latencyMs = 10,
        decimal costPer1KTokens = 0.01m,
        int qualityScore = 80,
        int complianceScore = 90,
        IReadOnlyList<string>? complianceTags = null)
    {
        return new RouterModelCandidate(
            ModelId: modelId,
            Provider: provider,
            Version: version,
            LatencyMs: latencyMs,
            CostPer1KTokens: costPer1KTokens,
            QualityScore: qualityScore,
            ComplianceScore: complianceScore,
            ComplianceTags: complianceTags ?? [ "standard" ]);
    }

    public static RouterSelectionResult CreateRoutingDecision(
        string taskClass = "workflow.hello",
        string? policyId = "test-policy",
        RouterModelCandidate? candidate = null,
        int? maxLatencyMs = 100,
        decimal? maxCostPer1KTokens = 0.1m,
        int? minQualityScore = 70,
        IReadOnlyList<string>? requiredComplianceTags = null,
        RouterSelectionWeights? effectiveWeights = null,
        decimal score = 0.9m,
        bool includeSelection = true)
    {
        var selectedCandidate = includeSelection ? candidate ?? CreateCandidate() : null;
        var rankedCandidates = selectedCandidate is null
            ? Array.Empty<RouterCandidateScore>()
            : [new RouterCandidateScore(selectedCandidate, score)];

        return new RouterSelectionResult(
            TaskClass: taskClass,
            Policy: new RouterSelectionPolicy(
                PolicyId: policyId,
                EffectiveConstraints: new RouterSelectionConstraints(
                    maxLatencyMs,
                    maxCostPer1KTokens,
                    minQualityScore,
                    requiredComplianceTags ?? [ "standard" ]),
                EffectiveWeights: effectiveWeights ?? new RouterSelectionWeights(0.25m, 0.25m, 0.25m, 0.25m)),
            SelectedCandidate: selectedCandidate,
            RankedCandidates: rankedCandidates,
            RejectionReasons: []);
    }
}
