using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public interface ISandboxSlot : IDisposable
{
    string SlotId { get; }

    ToolExecutionResult Execute(ToolExecutionRequest request);
}
