namespace Aos.WebApi.Models;

public sealed record ToolExecutionRequest(
    string InvocationId,
    ToolRef Tool,
    string InputJson
);

public sealed record ToolExecutionResult(
    string InvocationId,
    ToolRef Tool,
    string Status,
    string InputJson,
    string OutputJson,
    string? Error
);

public sealed record ToolExecutionEvent(
    string InvocationId,
    string ToolId,
    string ToolVersion,
    string Status,
    string InputJson,
    string OutputJson,
    string? Error
);
