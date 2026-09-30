using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public sealed class DiagnosticsRuleRouter : IRouterService
{
    public RouterSelectionResult SelectModel(RouterSelectionRequest request)
    {
        var candidate = new RouterModelCandidate("deterministic-playbooks", "local", "1.0", 0, 0, 100, 100, ["offline", "standard"]);
        return new(request.TaskClass, new("diagnostics-local-rules", new(null, null, null, ["offline", "standard"]),
            new(0, 0, 1, 0)), candidate, [new(candidate, 100)], []);
    }
}
