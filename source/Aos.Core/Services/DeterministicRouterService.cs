using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Microsoft.Extensions.Options;

namespace Aos.WebApi.Services;

public sealed class DeterministicRouterService : IRouterService
{
    private readonly CompiledRouterCatalog _catalog;
    private readonly RouterMetricsOptions _metricsOptions;
    private readonly IRouterMetricsStore _metricsStore;

    public DeterministicRouterService(IOptions<RouterOptions> options)
        : this(
            options,
            Microsoft.Extensions.Options.Options.Create(new RouterMetricsOptions()),
            new InMemoryRouterMetricsStore([]))
    {
    }

    public DeterministicRouterService(
        IOptions<RouterOptions> options,
        IOptions<RouterMetricsOptions> metricsOptions,
        IRouterMetricsStore metricsStore)
    {
        _catalog = RouterCatalogCompiler.Compile(options.Value);
        _metricsOptions = metricsOptions.Value;
        _metricsStore = metricsStore;

        if (_metricsOptions.BlendWeight is < 0m or > 1m)
        {
            throw new InvalidOperationException("RouterMetrics.BlendWeight must be between 0 and 1.");
        }
    }

    public RouterSelectionResult SelectModel(RouterSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.TaskClass))
        {
            throw new ArgumentException("Task class is required.", nameof(request));
        }

        return RouterSelectionEngine.Select(
            request.TaskClass.Trim(),
            _catalog,
            request,
            _metricsOptions,
            _metricsStore);
    }
}
