using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

// v1: in-process isolation. IDisposable is a hook for July's real OS-level sandbox teardown.
public sealed class InProcessSandboxSlot : IDisposable
{
    private readonly IToolExecutor _executor;
    private bool _disposed;

    public InProcessSandboxSlot()
    {
        _executor = new DeterministicEchoToolExecutor();
    }

    public ToolExecutionResult Execute(ToolExecutionRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _executor.Execute(request);
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
