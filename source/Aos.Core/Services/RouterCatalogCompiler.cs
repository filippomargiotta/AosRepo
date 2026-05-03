using Aos.WebApi.Models;
using Aos.WebApi.Options;

namespace Aos.WebApi.Services;

internal static class RouterCatalogCompiler
{
    public static CompiledRouterCatalog Compile(RouterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Candidates.Count == 0)
        {
            throw new InvalidOperationException("Router.Candidates must contain at least one entry.");
        }

        ValidateWeights(options.Weights, "Router.Weights");

        var duplicateIds = options.Candidates
            .GroupBy(candidate => $"{candidate.Provider}:{candidate.ModelId}:{candidate.Version}", StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicateIds.Length > 0)
        {
            throw new InvalidOperationException(
                $"Router.Candidates contains duplicate provider/model/version entries: {string.Join(", ", duplicateIds)}.");
        }

        var duplicatePolicies = options.Policies
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

        var defaultWeights = NormalizeWeights(options.Weights);
        var candidates = options.Candidates.Select(MapCandidate).ToArray();
        var policies = new Dictionary<string, CompiledRouterPolicy>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < options.Policies.Count; i++)
        {
            var policy = options.Policies[i];
            if (string.IsNullOrWhiteSpace(policy.TaskClass))
            {
                throw new InvalidOperationException($"Router.Policies[{i}].TaskClass is required.");
            }

            if (policy.Weights is not null)
            {
                ValidateWeights(policy.Weights, $"Router.Policies[{i}].Weights");
            }

            policies[policy.TaskClass.Trim()] = new CompiledRouterPolicy(
                PolicyId: string.IsNullOrWhiteSpace(policy.PolicyId) ? null : policy.PolicyId,
                MaxLatencyMs: policy.MaxLatencyMs,
                MaxCostPer1KTokens: policy.MaxCostPer1KTokens,
                MinQualityScore: policy.MinQualityScore,
                RequiredComplianceTags: NormalizeTags(policy.RequiredComplianceTags).OrderBy(tag => tag, StringComparer.Ordinal).ToArray(),
                Weights: policy.Weights is null ? defaultWeights : NormalizeWeights(policy.Weights));
        }

        return new CompiledRouterCatalog(defaultWeights, candidates, policies);
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

    private static void ValidateWeights(RouterWeightsOptions weights, string path)
    {
        var weightTotal = weights.Latency + weights.Cost + weights.Quality + weights.Compliance;
        if (weightTotal <= 0)
        {
            throw new InvalidOperationException($"{path} must sum to a positive value.");
        }
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

    private static IReadOnlySet<string> NormalizeTags(IEnumerable<string>? tags)
    {
        return tags?
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
    }
}
