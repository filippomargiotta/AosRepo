using System.Diagnostics;
using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Microsoft.Extensions.Options;

namespace Aos.WebApi.Services;

public sealed class PooledSandboxToolExecutor : IToolExecutor, IDisposable
{
    private readonly PreWarmedSandboxPool _pool;
    private readonly string _executorType;

    public PooledSandboxToolExecutor(PreWarmedSandboxPool pool, IOptions<SandboxPoolOptions> options)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(options);
        _pool = pool;
        _executorType = options.Value.ExecutorType;
    }

    public ToolExecutionResult Execute(ToolExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var acquireStart = Stopwatch.GetTimestamp();
        var (slot, wasWarm) = _pool.Acquire();
        var acquireLatencyMs = ToMilliseconds(Stopwatch.GetTimestamp() - acquireStart);

        try
        {
            var execStart = Stopwatch.GetTimestamp();
            var result = slot.Execute(request);
            var execLatencyMs = ToMilliseconds(Stopwatch.GetTimestamp() - execStart);

            return result with
            {
                SandboxExecution = new SandboxExecutionInfo(
                    WarmStart: wasWarm,
                    AcquireLatencyMs: acquireLatencyMs,
                    ExecutionLatencyMs: execLatencyMs,
                    ExecutorType: _executorType)
            };
        }
        finally
        {
            _pool.Release(slot);
        }
    }

    private static double ToMilliseconds(long ticks) =>
        ticks * 1000.0 / Stopwatch.Frequency;

    public void Dispose()
    {
        _pool.Dispose();
    }
}
