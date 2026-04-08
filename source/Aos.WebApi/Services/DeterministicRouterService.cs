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

        var requiredTags = NormalizeTags(request.RequiredComplianceTags);
        var rankedCandidates = new List<RouterCandidateScore>();
        var rejectionReasons = new List<string>();

        foreach (var candidate in _options.Candidates.Select(MapCandidate))
        {
            var candidateRejections = GetCandidateRejections(candidate, request, requiredTags);
            if (candidateRejections.Count > 0)
            {
                rejectionReasons.AddRange(candidateRejections);
                continue;
            }

            rankedCandidates.Add(new RouterCandidateScore(candidate, ComputeScore(candidate, request)));
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
            TaskClass: request.TaskClass,
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

        var weightTotal = _options.Weights.Latency + _options.Weights.Cost + _options.Weights.Quality + _options.Weights.Compliance;
        if (weightTotal <= 0)
        {
            throw new InvalidOperationException("Router.Weights must sum to a positive value.");
        }

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
    }

    private List<string> GetCandidateRejections(
        RouterModelCandidate candidate,
        RouterSelectionRequest request,
        IReadOnlySet<string> requiredTags)
    {
        var rejections = new List<string>();

        if (request.MaxLatencyMs is int maxLatency && candidate.LatencyMs > maxLatency)
        {
            rejections.Add(
                $"Candidate {candidate.Provider}/{candidate.ModelId}/{candidate.Version} rejected: latency {candidate.LatencyMs}ms exceeds max {maxLatency}ms.");
        }

        if (request.MaxCostPer1KTokens is decimal maxCost && candidate.CostPer1KTokens > maxCost)
        {
            rejections.Add(
                $"Candidate {candidate.Provider}/{candidate.ModelId}/{candidate.Version} rejected: cost {candidate.CostPer1KTokens:0.###} exceeds max {maxCost:0.###}.");
        }

        if (request.MinQualityScore is int minQuality && candidate.QualityScore < minQuality)
        {
            rejections.Add(
                $"Candidate {candidate.Provider}/{candidate.ModelId}/{candidate.Version} rejected: quality {candidate.QualityScore} is below min {minQuality}.");
        }

        var missingTags = requiredTags
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

    private decimal ComputeScore(RouterModelCandidate candidate, RouterSelectionRequest request)
    {
        var weights = NormalizeWeights();
        var latencyScore = request.MaxLatencyMs is int maxLatency
            ? 1m - ((decimal)candidate.LatencyMs / maxLatency)
            : 1000m / (1000m + candidate.LatencyMs);
        var costScore = request.MaxCostPer1KTokens is decimal maxCost
            ? 1m - (candidate.CostPer1KTokens / maxCost)
            : 1m / (1m + candidate.CostPer1KTokens);
        var qualityFloor = request.MinQualityScore ?? 0;
        var qualityRange = Math.Max(1, 100 - qualityFloor);
        var qualityScore = request.MinQualityScore is int
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

    private RouterWeightsOptions NormalizeWeights()
    {
        var total = _options.Weights.Latency + _options.Weights.Cost + _options.Weights.Quality + _options.Weights.Compliance;
        return new RouterWeightsOptions
        {
            Latency = _options.Weights.Latency / total,
            Cost = _options.Weights.Cost / total,
            Quality = _options.Weights.Quality / total,
            Compliance = _options.Weights.Compliance / total
        };
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

    private static decimal Clamp01(decimal value) => decimal.Max(0m, decimal.Min(1m, value));
}
