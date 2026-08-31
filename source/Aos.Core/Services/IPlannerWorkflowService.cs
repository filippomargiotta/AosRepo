using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public interface IPlannerWorkflowService
{
    PlannerWorkflowArtifacts CreateArtifacts(
        string runId,
        PlannerTaskRequest task,
        CancellationToken cancellationToken = default);
}
