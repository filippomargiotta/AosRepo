using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public interface IPlannerCandidateProvider
{
    PlannerCandidateResult CreateCandidate(PlannerTaskRequest task, PlaybookMatch selectedPlaybook);
}
