using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public sealed class DeterministicEchoToolExecutor : IToolExecutor
{
    public ToolExecutionResult Execute(ToolExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.RunId))
        {
            throw new ArgumentException("Tool run id is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.InvocationId))
        {
            throw new ArgumentException("Tool invocation id is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Tool.ToolId))
        {
            throw new ArgumentException("Tool id is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Tool.Version))
        {
            throw new ArgumentException("Tool version is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Action))
        {
            throw new ArgumentException("Tool action is required.", nameof(request));
        }

        return new ToolExecutionResult(
            InvocationId: request.InvocationId,
            Tool: request.Tool,
            Status: "succeeded",
            InputJson: request.InputJson,
            OutputJson: request.InputJson,
            Error: null);
    }
}
