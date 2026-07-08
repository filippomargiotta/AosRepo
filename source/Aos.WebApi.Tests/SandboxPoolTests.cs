using System.Text.Json;
using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Aos.WebApi.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aos.WebApi.Tests;

public sealed class SandboxPoolTests
{
    // --- ProcessSandboxSlot ---

    [Fact]
    public void ProcessSlot_Execute_RunsToolInSubprocess()
    {
        using var slot = new ProcessSandboxSlot();

        var result = slot.Execute(CreateRequest(inputJson: "{\"message\":\"from-process\"}"));

        Assert.Equal("succeeded", result.Status);
        Assert.Equal("{\"message\":\"from-process\"}", result.OutputJson);
    }

    [Fact]
    public void ProcessSlot_AfterDispose_RejectsFurtherExecution()
    {
        var slot = new ProcessSandboxSlot();
        slot.Dispose();

        Assert.Throws<ObjectDisposedException>(() => slot.Execute(CreateRequest()));
    }

    [Fact]
    public void ProcessSlot_WhenWorkerCrashes_ReturnsStableErrorResult()
    {
        using var slot = new ProcessSandboxSlot(environmentVariables: EnableWorkerTestCommands());

        var result = slot.Execute(CreateRequest(inputJson: "{\"sandboxTest\":\"crash\"}"));

        Assert.Equal("failed", result.Status);
        Assert.Equal("{}", result.OutputJson);
        Assert.Equal("sandbox_process_exit", result.Error);
    }

    [Fact]
    public void ProcessSlot_WhenWorkerTimesOut_ReturnsStableErrorResult()
    {
        using var slot = new ProcessSandboxSlot(
            executionTimeout: TimeSpan.FromMilliseconds(100),
            environmentVariables: EnableWorkerTestCommands());

        var result = slot.Execute(CreateRequest(inputJson: "{\"sandboxTest\":\"timeout\"}"));

        Assert.Equal("failed", result.Status);
        Assert.Equal("{}", result.OutputJson);
        Assert.Equal("sandbox_timeout", result.Error);
    }

    [Fact]
    public void ProcessSlot_TestCommandsAreIgnoredByDefault()
    {
        using var slot = new ProcessSandboxSlot();

        var result = slot.Execute(CreateRequest(inputJson: "{\"sandboxTest\":\"crash\"}"));

        Assert.Equal("succeeded", result.Status);
        Assert.Equal("{\"sandboxTest\":\"crash\"}", result.OutputJson);
    }

    [Fact]
    public void ProcessSlot_DrainsWorkerStderr()
    {
        using var slot = new ProcessSandboxSlot(
            executionTimeout: TimeSpan.FromSeconds(5),
            environmentVariables: EnableWorkerTestCommands());

        var result = slot.Execute(CreateRequest(inputJson: "{\"sandboxTest\":\"stderr\"}"));

        Assert.Equal("succeeded", result.Status);
        Assert.Equal("{\"sandboxTest\":\"stderr\"}", result.OutputJson);
    }

    // --- PreWarmedSandboxPool ---

    [Fact]
    public void Pool_WhenPreWarmed_ReturnsWarmStartOnFirstAcquire()
    {
        using var pool = new PreWarmedSandboxPool(poolSize: 1);

        var (slot, wasWarm) = pool.Acquire();
        pool.Release(slot);

        Assert.True(wasWarm);
    }

    [Fact]
    public void Pool_WhenEmpty_ReturnsColdStartAndCreatesSlot()
    {
        using var pool = new PreWarmedSandboxPool(poolSize: 0);

        var (slot, wasWarm) = pool.Acquire();

        Assert.False(wasWarm);
        Assert.NotNull(slot);
        slot.Dispose();
    }

    [Fact]
    public void Pool_AfterRelease_RefilsToConfiguredSize()
    {
        using var pool = new PreWarmedSandboxPool(poolSize: 2);
        var (slot, _) = pool.Acquire();
        Assert.Equal(1, pool.CurrentPoolSize);

        pool.Release(slot);

        Assert.True(WaitUntil(() => pool.CurrentPoolSize == 2), "Pool did not refill to configured size.");
    }

    [Fact]
    public void Pool_DoesNotExceedMaxPoolSize_OnRelease()
    {
        using var pool = new PreWarmedSandboxPool(poolSize: 1);
        var (firstSlot, _) = pool.Acquire();
        var (secondSlot, _) = pool.Acquire();

        pool.Release(firstSlot);
        secondSlot.Dispose();

        Assert.True(pool.CurrentPoolSize <= 1);
    }

    [Fact]
    public void Pool_ConcurrentAcquireAndRelease_IsThreadSafeWithNoExceptions()
    {
        const int poolSize = 3;
        const int threads = 20;
        using var pool = new PreWarmedSandboxPool(poolSize);
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
        using var pool = new PreWarmedSandboxPool(poolSize);
        var barrier = new Barrier(threads);
        var warmCount = 0;
        var slots = new ProcessSandboxSlot[threads];

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

    [Fact]
    public void Pool_ReleaseRefillsWithFreshProcessSlot()
    {
        using var pool = new PreWarmedSandboxPool(poolSize: 1);
        var (firstSlot, _) = pool.Acquire();
        var firstProcessId = firstSlot.ProcessId;

        pool.Release(firstSlot);
        Assert.True(WaitUntil(() => pool.CurrentPoolSize == 1), "Pool did not refill after release.");
        var (secondSlot, _) = pool.Acquire();
        var secondProcessId = secondSlot.ProcessId;
        pool.Release(secondSlot);

        Assert.NotEqual(firstProcessId, secondProcessId);
    }

    [Fact]
    public void Pool_ReleaseSchedulesReplacementWithoutBlockingCaller()
    {
        var factoryCalls = 0;
        using var pool = new PreWarmedSandboxPool(
            poolSize: 1,
            slotFactory: () =>
            {
                if (Interlocked.Increment(ref factoryCalls) > 1)
                {
                    Thread.Sleep(300);
                }

                return new ProcessSandboxSlot();
            });
        var (slot, _) = pool.Acquire();

        var startedAt = DateTimeOffset.UtcNow;
        pool.Release(slot);
        var elapsed = DateTimeOffset.UtcNow - startedAt;

        Assert.True(elapsed < TimeSpan.FromMilliseconds(150), $"Release blocked for {elapsed.TotalMilliseconds:0.0} ms.");
        Assert.True(WaitUntil(() => pool.CurrentPoolSize == 1), "Background refill did not complete.");
    }

    [Fact]
    public void Pool_DisposeDuringPendingRefill_DoesNotKeepReplacementQueued()
    {
        var factoryCalls = 0;
        var pool = new PreWarmedSandboxPool(
            poolSize: 1,
            slotFactory: () =>
            {
                if (Interlocked.Increment(ref factoryCalls) > 1)
                {
                    Thread.Sleep(300);
                }

                return new ProcessSandboxSlot();
            });
        var (slot, _) = pool.Acquire();

        pool.Release(slot);
        pool.Dispose();

        Thread.Sleep(700);
        Assert.Equal(0, pool.CurrentPoolSize);
    }

    // --- PooledSandboxToolExecutor ---

    [Fact]
    public void PooledExecutor_WarmStart_PopulatesSandboxExecutionInfo()
    {
        using var executor = CreateExecutor(poolSize: 1);

        var result = executor.Execute(CreateRequest());

        Assert.NotNull(result.SandboxExecution);
        Assert.True(result.SandboxExecution.WarmStart);
    }

    [Fact]
    public void PooledExecutor_ColdStart_PopulatesSandboxExecutionInfo()
    {
        using var executor = CreateExecutor(poolSize: 0);

        var result = executor.Execute(CreateRequest());

        Assert.NotNull(result.SandboxExecution);
        Assert.False(result.SandboxExecution.WarmStart);
    }

    [Fact]
    public void PooledExecutor_AlwaysPopulatesNonNegativeLatencies()
    {
        using var executor = CreateExecutor(poolSize: 2);

        var result = executor.Execute(CreateRequest());

        Assert.NotNull(result.SandboxExecution);
        Assert.True(result.SandboxExecution.AcquireLatencyMs >= 0);
        Assert.True(result.SandboxExecution.ExecutionLatencyMs >= 0);
    }

    [Fact]
    public void PooledExecutor_SetsExecutorType()
    {
        using var executor = CreateExecutor(poolSize: 1, executorType: "process-v1");

        var result = executor.Execute(CreateRequest());

        Assert.Equal("process-v1", result.SandboxExecution!.ExecutorType);
    }

    [Fact]
    public void PooledExecutor_RefillsPoolAfterEachExecution()
    {
        using var pool = new PreWarmedSandboxPool(poolSize: 1);
        using var executor = new PooledSandboxToolExecutor(pool, Microsoft.Extensions.Options.Options.Create(new SandboxPoolOptions { PoolSize = 1 }));

        executor.Execute(CreateRequest());
        executor.Execute(CreateRequest());
        var result = executor.Execute(CreateRequest());

        Assert.NotNull(result.SandboxExecution);
        Assert.Equal("succeeded", result.Status);
    }

    [Fact]
    public void PooledExecutor_DoesNotPersistSandboxInfoToToolEvent()
    {
        using var executor = CreateExecutor(poolSize: 1);
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
        using var pool = new PreWarmedSandboxPool(poolSize: 2);
        var options = Microsoft.Extensions.Options.Options.Create(new SandboxPoolOptions { PoolSize = 2, ExecutorType = "process-v1" });
        using var pooledExecutor = new PooledSandboxToolExecutor(pool, options);
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
        using var pool = new PreWarmedSandboxPool(poolSize: 2);
        var options = Microsoft.Extensions.Options.Options.Create(new SandboxPoolOptions { PoolSize = 2, ExecutorType = "process-v1" });
        using var executor = new PooledSandboxToolExecutor(pool, options);

        var report = SandboxBenchmarkRunner.Run(
            executor,
            poolSize: 2,
            executorType: "process-v1",
            new SandboxBenchmarkOptions(Iterations: 20, WarmupIterations: 0));

        Assert.Equal(20, report.WarmStartCount + report.ColdStartCount);
        Assert.True(report.WarmStartCount > 0, "Expected at least some warm starts with pool size 2.");
    }

    [Fact]
    public void BenchmarkRunner_AllColdWithZeroPoolSize()
    {
        using var pool = new PreWarmedSandboxPool(poolSize: 0);
        var options = Microsoft.Extensions.Options.Options.Create(new SandboxPoolOptions { PoolSize = 0, ExecutorType = "process-v1" });
        using var executor = new PooledSandboxToolExecutor(pool, options);

        var report = SandboxBenchmarkRunner.Run(
            executor,
            poolSize: 0,
            executorType: "process-v1",
            new SandboxBenchmarkOptions(Iterations: 10, WarmupIterations: 0));

        Assert.Equal(10, report.ColdStartCount);
        Assert.Equal(0, report.WarmStartCount);
    }

    private static PooledSandboxToolExecutor CreateExecutor(int poolSize, string executorType = "process-v1")
    {
        var pool = new PreWarmedSandboxPool(poolSize);
        var options = Microsoft.Extensions.Options.Options.Create(new SandboxPoolOptions { PoolSize = poolSize, ExecutorType = executorType });
        return new PooledSandboxToolExecutor(pool, options);
    }

    private static ToolExecutionRequest CreateRequest(string inputJson = "{\"test\":true}") =>
        new(
            RunId: "test-run",
            InvocationId: "test-run:tool:0",
            Tool: new ToolRef("echo", "1.0"),
            Action: "tool.execute",
            InputJson: inputJson,
            CapabilityToken: string.Empty,
            RequestedAtUtc: DateTimeOffset.UtcNow);

    private static IReadOnlyDictionary<string, string> EnableWorkerTestCommands() =>
        new Dictionary<string, string>
        {
            [ProcessSandboxSlot.TestCommandsEnvironmentVariable] = "1"
        };

    private static bool WaitUntil(Func<bool> predicate, int timeoutMs = 2_000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return predicate();
    }
}
