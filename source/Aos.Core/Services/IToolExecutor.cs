using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public interface IToolExecutor
{
    ToolExecutionResult Execute(ToolExecutionRequest request);
}
