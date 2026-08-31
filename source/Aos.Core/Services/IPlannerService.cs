using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public interface IPlannerService
{
    PlannerPlanningResult Plan(PlannerTaskRequest task);
}
