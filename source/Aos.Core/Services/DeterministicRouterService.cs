using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Microsoft.Extensions.Options;

namespace Aos.WebApi.Services;

public sealed class DeterministicRouterService : IRouterService
{
    private readonly CompiledRouterCatalog _catalog;

    public DeterministicRouterService(IOptions<RouterOptions> options)
    {
        _catalog = RouterCatalogCompiler.Compile(options.Value);
    }

    public RouterSelectionResult SelectModel(RouterSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.TaskClass))
        {
            throw new ArgumentException("Task class is required.", nameof(request));
        }

        return RouterSelectionEngine.Select(request.TaskClass.Trim(), _catalog, request);
    }
}
