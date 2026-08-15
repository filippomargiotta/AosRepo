using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public interface IPlaybookStore
{
    IReadOnlyList<PlannerPlaybook> ListByTaskClass(string taskClass);
}
