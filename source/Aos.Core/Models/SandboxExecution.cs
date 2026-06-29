namespace Aos.WebApi.Models;

public sealed record SandboxExecutionInfo(
    bool WarmStart,
    double AcquireLatencyMs,
    double ExecutionLatencyMs,
    string ExecutorType
);
