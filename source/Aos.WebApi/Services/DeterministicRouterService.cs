using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Microsoft.Extensions.Options;

namespace Aos.WebApi.Services;

public sealed class DeterministicRouterService : IRouterService
{
    private readonly RouterOptions _options;

    public DeterministicRouterService(IOptions<RouterOptions> options)
    {
        _options = options.Value;
    }

    public RouterSelectionResult SelectModel(RouterSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.TaskClass))
        {
            throw new ArgumentException("Task class is required.", nameof(request));
        }

        ValidateOptions();

        var taskClass = request.TaskClass.Trim();
        var policy = ResolvePolicy(taskClass);
        var effectiveConstraints = ResolveConstraints(request, policy);
        var effectiveWeights = NormalizeWeights(policy?.Weights ?? _options.Weights);
        var rankedCandidates = new List<RouterCandidateScore>();
        var rejectionReasons = new List<string>();

        foreach (var candidate in _options.Candidates.Select(MapCandidate))
        {
            var candidateRejections = GetCandidateRejections(candidate, effectiveConstraints);
            if (candidateRejections.Count > 0)
            {
                rejectionReasons.AddRange(candidateRejections);
                continue;
            }

            rankedCandidates.Add(new RouterCandidateScore(candidate, ComputeScore(candidate, effectiveConstraints, effectiveWeights)));
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
                PolicyId: string.IsNullOrWhiteSpace(policy?.PolicyId) ? null : policy.PolicyId,
                EffectiveConstraints: effectiveConstraints,
                EffectiveWeights: effectiveWeights),
            SelectedCandidate: orderedCandidates.FirstOrDefault()?.Candidate,
            RankedCandidates: orderedCandidates,
            RejectionReasons: rejectionReasons);
    }

    private void ValidateOptions()
    {
        if (_options.Candidates.Count == 0)
        {
            throw new InvalidOperationException("Router.Candidates must contain at least one entry.");
        }

        ValidateWeights(_options.Weights, "Router.Weights");

        var duplicateIds = _options.Candidates
            .GroupBy(candidate => $"{candidate.Provider}:{candidate.ModelId}:{candidate.Version}", StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicateIds.Length > 0)
        {
            throw new InvalidOperationException(
                $"Router.Candidates contains duplicate provider/model/version entries: {string.Join(", ", duplicateIds)}.");
        }

        var duplicatePolicies = _options.Policies
            .Where(policy => !string.IsNullOrWhiteSpace(policy.TaskClass))
            .GroupBy(policy => policy.TaskClass.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicatePolicies.Length > 0)
        {
            throw new InvalidOperationException(
                $"Router.Policies contains duplicate taskClass entries: {string.Join(", ", duplicatePolicies)}.");
        }

        for (var i = 0; i < _options.Policies.Count; i++)
        {
            var policy = _options.Policies[i];
            if (string.IsNullOrWhiteSpace(policy.TaskClass))
            {
                throw new InvalidOperationException($"Router.Policies[{i}].TaskClass is required.");
            }

            if (policy.Weights is not null)
            {
                ValidateWeights(policy.Weights, $"Router.Policies[{i}].Weights");
            }
        }
    }

    private List<string> GetCandidateRejections(
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

    private decimal ComputeScore(
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

    private RouterPolicyOptions? ResolvePolicy(string taskClass)
    {
        return _options.Policies.FirstOrDefault(policy =>
            string.Equals(policy.TaskClass?.Trim(), taskClass, StringComparison.OrdinalIgnoreCase));
    }

    private static RouterSelectionConstraints ResolveConstraints(
        RouterSelectionRequest request,
        RouterPolicyOptions? policy)
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

    private static RouterSelectionWeights NormalizeWeights(RouterWeightsOptions weights)
    {
        var total = weights.Latency + weights.Cost + weights.Quality + weights.Compliance;
        return new RouterSelectionWeights(
            Latency: weights.Latency / total,
            Cost: weights.Cost / total,
            Quality: weights.Quality / total,
            Compliance: weights.Compliance / total);
    }

    private static void ValidateWeights(RouterWeightsOptions weights, string path)
    {
        var weightTotal = weights.Latency + weights.Cost + weights.Quality + weights.Compliance;
        if (weightTotal <= 0)
        {
            throw new InvalidOperationException($"{path} must sum to a positive value.");
        }
    }

    private static RouterModelCandidate MapCandidate(RouterModelOptions candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.ModelId))
        {
            throw new InvalidOperationException("Router.Candidates[].ModelId is required.");
        }

        if (string.IsNullOrWhiteSpace(candidate.Provider))
        {
            throw new InvalidOperationException("Router.Candidates[].Provider is required.");
        }

        if (string.IsNullOrWhiteSpace(candidate.Version))
        {
            throw new InvalidOperationException("Router.Candidates[].Version is required.");
        }

        return new RouterModelCandidate(
            ModelId: candidate.ModelId,
            Provider: candidate.Provider,
            Version: candidate.Version,
            LatencyMs: candidate.LatencyMs,
            CostPer1KTokens: candidate.CostPer1KTokens,
            QualityScore: candidate.QualityScore,
            ComplianceScore: candidate.ComplianceScore,
            ComplianceTags: NormalizeTags(candidate.ComplianceTags).ToArray());
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
}
