using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public sealed class CapabilityEnforcingToolExecutor : IToolExecutor
{
    private readonly ICapabilityTokenValidator _validator;
    private readonly IToolExecutor _innerExecutor;

    public CapabilityEnforcingToolExecutor(
        ICapabilityTokenValidator validator,
        IToolExecutor innerExecutor)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _innerExecutor = innerExecutor ?? throw new ArgumentNullException(nameof(innerExecutor));
    }

    public ToolExecutionResult Execute(ToolExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scope = new ToolCapabilityScope(
            request.RunId,
            request.InvocationId,
            request.Tool.ToolId,
            request.Tool.Version,
            request.Action);
        var decision = _validator.Validate(
            request.CapabilityToken,
            scope,
            request.RequestedAtUtc);

        if (!string.Equals(decision.Decision, "allow", StringComparison.Ordinal))
        {
            return new ToolExecutionResult(
                InvocationId: request.InvocationId,
                Tool: request.Tool,
                Status: "denied",
                InputJson: request.InputJson,
                OutputJson: "{}",
                Error: "capability_denied",
                CapabilityDecision: decision);
        }

        var sanitizedRequest = request with { CapabilityToken = string.Empty };
        var result = _innerExecutor.Execute(sanitizedRequest);
        return result with { CapabilityDecision = decision };
    }
}
