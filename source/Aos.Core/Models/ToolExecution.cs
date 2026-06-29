namespace Aos.WebApi.Models;

public sealed record ToolExecutionRequest(
    string RunId,
    string InvocationId,
    ToolRef Tool,
    string Action,
    string InputJson,
    string CapabilityToken,
    DateTimeOffset RequestedAtUtc
);

public sealed record ToolExecutionResult(
    string InvocationId,
    ToolRef Tool,
    string Status,
    string InputJson,
    string OutputJson,
    string? Error,
    CapabilityDecision? CapabilityDecision = null,
    SandboxExecutionInfo? SandboxExecution = null
);

public sealed record ToolExecutionEvent(
    string InvocationId,
    string ToolId,
    string ToolVersion,
    string Status,
    string InputJson,
    string OutputJson,
    string? Error,
    CapabilityDecision CapabilityDecision
);

public sealed record ToolCapabilityScope(
    string RunId,
    string InvocationId,
    string ToolId,
    string ToolVersion,
    string Action
);

public sealed record CapabilityDecision(
    string PolicyId,
    string Decision,
    string ReasonCode,
    string? TokenId
);
