using System.Text.Json;
using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Aos.WebApi.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aos.WebApi.Tests;

public sealed class SandboxPoolTests
{
    // --- PreWarmedSandboxPool ---

    [Fact]
    public void Pool_WhenPreWarmed_ReturnsWarmStartOnFirstAcquire()
    {
        var pool = new PreWarmedSandboxPool(poolSize: 1);

        var (_, wasWarm) = pool.Acquire();

        Assert.True(wasWarm);
    }

    [Fact]
    public void Pool_WhenEmpty_ReturnsColdStartAndCreatesSlot()
    {
        var pool = new PreWarmedSandboxPool(poolSize: 0);

        var (slot, wasWarm) = pool.Acquire();

        Assert.False(wasWarm);
        Assert.NotNull(slot);
        slot.Dispose();
    }

    [Fact]
    public void Pool_AfterRelease_RefilsToConfiguredSize()
    {
        var pool = new PreWarmedSandboxPool(poolSize: 2);
        var (slot, _) = pool.Acquire();
        Assert.Equal(1, pool.CurrentPoolSize);

        pool.Release(slot);

        Assert.Equal(2, pool.CurrentPoolSize);
    }

    [Fact]
    public void Pool_DoesNotExceedMaxPoolSize_OnRelease()
    {
        var pool = new PreWarmedSandboxPool(poolSize: 1);
        var (coldSlot, _) = pool.Acquire();
        var (_, _) = pool.Acquire();

        pool.Release(coldSlot);

        Assert.Equal(1, pool.CurrentPoolSize);
    }

    [Fact]
    public void Pool_ConcurrentAcquireAndRelease_IsThreadSafeWithNoExceptions()
    {
        const int poolSize = 3;
        const int threads = 20;
        var pool = new PreWarmedSandboxPool(poolSize);
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        Parallel.For(0, threads, _ =>
        {
            try
            {
                var (slot, _) = pool.Acquire();
                pool.Release(slot);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.Empty(exceptions);
    }

    [Fact]
    public void Pool_SimultaneousDrain_WarmsStartsDoNotExceedPoolSize()
    {
        const int poolSize = 3;
        const int threads = 10;
        var pool = new PreWarmedSandboxPool(poolSize);
        var barrier = new Barrier(threads);
        var warmCount = 0;
        var slots = new InProcessSandboxSlot[threads];

        Parallel.For(0, threads, i =>
        {
            barrier.SignalAndWait();
            var (slot, wasWarm) = pool.Acquire();
            slots[i] = slot;
            if (wasWarm)
            {
                Interlocked.Increment(ref warmCount);
            }
        });

        foreach (var slot in slots)
        {
            pool.Release(slot);
        }

        Assert.True(warmCount <= poolSize, $"Warm starts ({warmCount}) exceeded pool size ({poolSize}).");
    }

    // --- PooledSandboxToolExecutor ---

    [Fact]
    public void PooledExecutor_WarmStart_PopulatesSandboxExecutionInfo()
    {
        var executor = CreateExecutor(poolSize: 1);

        var result = executor.Execute(CreateRequest());

        Assert.NotNull(result.SandboxExecution);
        Assert.True(result.SandboxExecution.WarmStart);
    }

    [Fact]
    public void PooledExecutor_ColdStart_PopulatesSandboxExecutionInfo()
    {
        var executor = CreateExecutor(poolSize: 0);

        var result = executor.Execute(CreateRequest());

        Assert.NotNull(result.SandboxExecution);
        Assert.False(result.SandboxExecution.WarmStart);
    }

    [Fact]
    public void PooledExecutor_AlwaysPopulatesNonNegativeLatencies()
    {
        var executor = CreateExecutor(poolSize: 2);

        var result = executor.Execute(CreateRequest());

        Assert.NotNull(result.SandboxExecution);
        Assert.True(result.SandboxExecution.AcquireLatencyMs >= 0);
        Assert.True(result.SandboxExecution.ExecutionLatencyMs >= 0);
    }

    [Fact]
    public void PooledExecutor_SetsExecutorType()
    {
        var executor = CreateExecutor(poolSize: 1, executorType: "in-process-v1");

        var result = executor.Execute(CreateRequest());

        Assert.Equal("in-process-v1", result.SandboxExecution!.ExecutorType);
    }

    [Fact]
    public void PooledExecutor_RefillsPoolAfterEachExecution()
    {
        var pool = new PreWarmedSandboxPool(poolSize: 1);
        var executor = new PooledSandboxToolExecutor(pool, Microsoft.Extensions.Options.Options.Create(new SandboxPoolOptions { PoolSize = 1 }));

        executor.Execute(CreateRequest());
        executor.Execute(CreateRequest());
        var result = executor.Execute(CreateRequest());

        Assert.NotNull(result.SandboxExecution);
        Assert.Equal("succeeded", result.Status);
    }

    [Fact]
    public void PooledExecutor_DoesNotPersistSandboxInfoToToolEvent()
    {
        var executor = CreateExecutor(poolSize: 1);
        var result = executor.Execute(CreateRequest());

        var eventJson = JsonSerializer.Serialize(new ToolExecutionEvent(
            InvocationId: result.InvocationId,
            ToolId: result.Tool.ToolId,
            ToolVersion: result.Tool.Version,
            Status: result.Status,
            InputJson: result.InputJson,
            OutputJson: result.OutputJson ?? "{}",
            Error: result.Error,
            CapabilityDecision: result.CapabilityDecision
                ?? new CapabilityDecision("none", "allow", "test", null)));

        Assert.DoesNotContain("sandboxExecution", eventJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("warmStart", eventJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("acquireLatency", eventJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PooledExecutor_WithCapabilityLayer_StillPopulatesSandboxInfo()
    {
        var pool = new PreWarmedSandboxPool(poolSize: 2);
        var options = Microsoft.Extensions.Options.Options.Create(new SandboxPoolOptions { PoolSize = 2, ExecutorType = "in-process-v1" });
        var pooledExecutor = new PooledSandboxToolExecutor(pool, options);
        var capabilityService = CapabilityTestData.CreateTokenService();
        var enforcingExecutor = new CapabilityEnforcingToolExecutor(capabilityService, pooledExecutor);

        var scope = new ToolCapabilityScope("run-1", "run-1:tool:0", "echo", "1.0", "tool.execute");
        var token = capabilityService.Issue(scope, DateTimeOffset.UtcNow);
        var request = new ToolExecutionRequest(
            RunId: "run-1",
            InvocationId: "run-1:tool:0",
            Tool: new ToolRef("echo", "1.0"),
            Action: "tool.execute",
            InputJson: "{}",
            CapabilityToken: token,
            RequestedAtUtc: DateTimeOffset.UtcNow);

        var result = enforcingExecutor.Execute(request);

        Assert.Equal("succeeded", result.Status);
        Assert.Equal("allow", result.CapabilityDecision!.Decision);
        Assert.NotNull(result.SandboxExecution);
        Assert.True(result.SandboxExecution.WarmStart);
    }

    // --- SandboxBenchmarkRunner ---

    [Fact]
    public void BenchmarkRunner_ReportsWarmAndColdCounts()
    {
        var pool = new PreWarmedSandboxPool(poolSize: 2);
        var options = Microsoft.Extensions.Options.Options.Create(new SandboxPoolOptions { PoolSize = 2, ExecutorType = "in-process-v1" });
        var executor = new PooledSandboxToolExecutor(pool, options);

        var report = SandboxBenchmarkRunner.Run(
            executor,
            poolSize: 2,
            executorType: "in-process-v1",
            new SandboxBenchmarkOptions(Iterations: 20, WarmupIterations: 0));

        Assert.Equal(20, report.WarmStartCount + report.ColdStartCount);
        Assert.True(report.WarmStartCount > 0, "Expected at least some warm starts with pool size 2.");
    }

    [Fact]
    public void BenchmarkRunner_AllColdWithZeroPoolSize()
    {
        var pool = new PreWarmedSandboxPool(poolSize: 0);
        var options = Microsoft.Extensions.Options.Options.Create(new SandboxPoolOptions { PoolSize = 0, ExecutorType = "in-process-v1" });
        var executor = new PooledSandboxToolExecutor(pool, options);

        var report = SandboxBenchmarkRunner.Run(
            executor,
            poolSize: 0,
            executorType: "in-process-v1",
            new SandboxBenchmarkOptions(Iterations: 10, WarmupIterations: 0));

        Assert.Equal(10, report.ColdStartCount);
        Assert.Equal(0, report.WarmStartCount);
    }

    private static PooledSandboxToolExecutor CreateExecutor(int poolSize, string executorType = "in-process-v1")
    {
        var pool = new PreWarmedSandboxPool(poolSize);
        var options = Microsoft.Extensions.Options.Options.Create(new SandboxPoolOptions { PoolSize = poolSize, ExecutorType = executorType });
        return new PooledSandboxToolExecutor(pool, options);
    }

    private static ToolExecutionRequest CreateRequest() =>
        new(
            RunId: "test-run",
            InvocationId: "test-run:tool:0",
            Tool: new ToolRef("echo", "1.0"),
            Action: "tool.execute",
            InputJson: "{\"test\":true}",
            CapabilityToken: string.Empty,
            RequestedAtUtc: DateTimeOffset.UtcNow);
}
