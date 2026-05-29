using Aos.WebApi.Models;
using Aos.WebApi.Options;

namespace Aos.WebApi.Services;

internal static class RouterSelectionEngine
{
    public static RouterSelectionResult Select(
        string taskClass,
        CompiledRouterCatalog catalog,
        RouterSelectionRequest request,
        RouterMetricsOptions? metricsOptions = null,
        IRouterMetricsStore? metricsStore = null)
    {
        var policy = catalog.ResolvePolicy(taskClass);
        var effectiveConstraints = ResolveConstraints(request, policy);
        var effectiveWeights = policy?.Weights ?? catalog.DefaultWeights;
        var metricsContext = RouterMetricsContext.Create(metricsOptions, metricsStore);
        var rankedCandidates = new List<RouterCandidateScore>();
        var rejectionReasons = new List<string>();

        foreach (var candidate in catalog.Candidates)
        {
            var candidateRejections = GetCandidateRejections(candidate, effectiveConstraints);
            if (candidateRejections.Count > 0)
            {
                rejectionReasons.AddRange(candidateRejections);
                continue;
            }

            var scoringCandidate = metricsContext.Apply(taskClass, candidate);
            rankedCandidates.Add(new RouterCandidateScore(
                candidate,
                ComputeScore(scoringCandidate, effectiveConstraints, effectiveWeights)));
        }

        var orderedCandidates = rankedCandidates
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Candidate.ComplianceScore)
            .ThenByDescending(item => item.Candidate.QualityScore)
            .ThenBy(item => item.Candidate.LatencyMs)
            .ThenBy(item => item.Candidate.CostPer1KTokens)
            .ThenBy(item => item.Candidate.Provider, StringComparer.Ordinal)
            .ThenBy(item => item.Candidate.ModelId, StringComparer.Ordinal)
            .ThenBy(item => item.Candidate.Version, StringComparer.Ordinal)
            .ToArray();

        return new RouterSelectionResult(
            TaskClass: taskClass,
            Policy: new RouterSelectionPolicy(
                PolicyId: policy?.PolicyId,
                EffectiveConstraints: effectiveConstraints,
                EffectiveWeights: effectiveWeights),
            SelectedCandidate: orderedCandidates.FirstOrDefault()?.Candidate,
            RankedCandidates: orderedCandidates,
            RejectionReasons: rejectionReasons);
    }

    private static List<string> GetCandidateRejections(
        RouterModelCandidate candidate,
        RouterSelectionConstraints constraints)
    {
        var rejections = new List<string>();

        if (constraints.MaxLatencyMs is int maxLatency && candidate.LatencyMs > maxLatency)
        {
            rejections.Add(
                $"Candidate {candidate.Provider}/{candidate.ModelId}/{candidate.Version} rejected: latency {candidate.LatencyMs}ms exceeds max {maxLatency}ms.");
        }

        if (constraints.MaxCostPer1KTokens is decimal maxCost && candidate.CostPer1KTokens > maxCost)
        {
            rejections.Add(
                $"Candidate {candidate.Provider}/{candidate.ModelId}/{candidate.Version} rejected: cost {candidate.CostPer1KTokens:0.###} exceeds max {maxCost:0.###}.");
        }

        if (constraints.MinQualityScore is int minQuality && candidate.QualityScore < minQuality)
        {
            rejections.Add(
                $"Candidate {candidate.Provider}/{candidate.ModelId}/{candidate.Version} rejected: quality {candidate.QualityScore} is below min {minQuality}.");
        }

        var missingTags = constraints.RequiredComplianceTags
            .Except(candidate.ComplianceTags, StringComparer.Ordinal)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToArray();

        if (missingTags.Length > 0)
        {
            rejections.Add(
                $"Candidate {candidate.Provider}/{candidate.ModelId}/{candidate.Version} rejected: missing compliance tags {string.Join(", ", missingTags)}.");
        }

        return rejections;
    }

    private static decimal ComputeScore(
        RouterModelCandidate candidate,
        RouterSelectionConstraints constraints,
        RouterSelectionWeights weights)
    {
        var latencyScore = constraints.MaxLatencyMs is int maxLatency
            ? 1m - ((decimal)candidate.LatencyMs / maxLatency)
            : 1000m / (1000m + candidate.LatencyMs);
        var costScore = constraints.MaxCostPer1KTokens is decimal maxCost
            ? 1m - (candidate.CostPer1KTokens / maxCost)
            : 1m / (1m + candidate.CostPer1KTokens);
        var qualityFloor = constraints.MinQualityScore ?? 0;
        var qualityRange = Math.Max(1, 100 - qualityFloor);
        var qualityScore = constraints.MinQualityScore is int
            ? (candidate.QualityScore - qualityFloor) / (decimal)qualityRange
            : candidate.QualityScore / 100m;
        var complianceScore = candidate.ComplianceScore / 100m;

        return decimal.Round(
            (weights.Latency * Clamp01(latencyScore)) +
            (weights.Cost * Clamp01(costScore)) +
            (weights.Quality * Clamp01(qualityScore)) +
            (weights.Compliance * Clamp01(complianceScore)),
            6,
            MidpointRounding.AwayFromZero);
    }

    private static RouterSelectionConstraints ResolveConstraints(
        RouterSelectionRequest request,
        CompiledRouterPolicy? policy)
    {
        var requiredTags = NormalizeTags(policy?.RequiredComplianceTags)
            .Union(NormalizeTags(request.RequiredComplianceTags), StringComparer.Ordinal)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToArray();

        return new RouterSelectionConstraints(
            MaxLatencyMs: MinNullable(request.MaxLatencyMs, policy?.MaxLatencyMs),
            MaxCostPer1KTokens: MinNullable(request.MaxCostPer1KTokens, policy?.MaxCostPer1KTokens),
            MinQualityScore: MaxNullable(request.MinQualityScore, policy?.MinQualityScore),
            RequiredComplianceTags: requiredTags);
    }

    private static IReadOnlySet<string> NormalizeTags(IEnumerable<string>? tags)
    {
        return tags?
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
    }

    private static int? MinNullable(int? first, int? second)
    {
        if (first is null)
        {
            return second;
        }

        return second is null ? first : Math.Min(first.Value, second.Value);
    }

    private static decimal? MinNullable(decimal? first, decimal? second)
    {
        if (first is null)
        {
            return second;
        }

        return second is null ? first : Math.Min(first.Value, second.Value);
    }

    private static int? MaxNullable(int? first, int? second)
    {
        if (first is null)
        {
            return second;
        }

        return second is null ? first : Math.Max(first.Value, second.Value);
    }

    private static decimal Clamp01(decimal value) => decimal.Max(0m, decimal.Min(1m, value));

    private sealed record RouterMetricsContext(
        bool Enabled,
        decimal BlendWeight,
        IRouterMetricsStore? Store)
    {
        public static RouterMetricsContext Create(
            RouterMetricsOptions? options,
            IRouterMetricsStore? store)
        {
            if (options is null || !options.Enabled || store is null || options.BlendWeight == 0m)
            {
                return new RouterMetricsContext(false, 0m, null);
            }

            return new RouterMetricsContext(true, options.BlendWeight, store);
        }

        public RouterModelCandidate Apply(string taskClass, RouterModelCandidate candidate)
        {
            if (!Enabled ||
                Store is null ||
                !Store.TryGetMetric(taskClass, candidate, out var metric) ||
                metric is null ||
                metric.SampleCount == 0)
            {
                return candidate;
            }

            return candidate with
            {
                LatencyMs = Blend(candidate.LatencyMs, metric.ObservedLatencyMs),
                QualityScore = Blend(
                    candidate.QualityScore,
                    (int)decimal.Round(
                        metric.QualityScore * metric.SuccessRate,
                        0,
                        MidpointRounding.AwayFromZero))
            };
        }

        private int Blend(int configuredValue, int metricValue)
        {
            var blended = (configuredValue * (1m - BlendWeight)) + (metricValue * BlendWeight);
            return (int)decimal.Round(blended, 0, MidpointRounding.AwayFromZero);
        }
    }
}
