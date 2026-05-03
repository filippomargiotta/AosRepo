using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

internal sealed class CompiledRouterCatalog
{
    private readonly IReadOnlyDictionary<string, CompiledRouterPolicy> _policiesByTaskClass;

    public CompiledRouterCatalog(
        RouterSelectionWeights defaultWeights,
        IReadOnlyList<RouterModelCandidate> candidates,
        IReadOnlyDictionary<string, CompiledRouterPolicy> policiesByTaskClass)
    {
        DefaultWeights = defaultWeights;
        Candidates = candidates;
        _policiesByTaskClass = policiesByTaskClass;
    }

    public RouterSelectionWeights DefaultWeights { get; }

    public IReadOnlyList<RouterModelCandidate> Candidates { get; }

    public CompiledRouterPolicy? ResolvePolicy(string taskClass) =>
        _policiesByTaskClass.GetValueOrDefault(taskClass);
}

internal sealed record CompiledRouterPolicy(
    string? PolicyId,
    int? MaxLatencyMs,
    decimal? MaxCostPer1KTokens,
    int? MinQualityScore,
    IReadOnlyList<string> RequiredComplianceTags,
    RouterSelectionWeights Weights
);
