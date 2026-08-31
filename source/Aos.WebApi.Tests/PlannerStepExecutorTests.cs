using Aos.WebApi.Models;
using Aos.WebApi.Services;
using Xunit;

namespace Aos.WebApi.Tests;

public sealed class PlannerStepExecutorTests
{
    private static readonly DateTimeOffset Instant = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Execute_RunsStepsStrictlyInSequenceWithCapabilityProtectedRequests()
    {
        var tokenService = CapabilityTestData.CreateTokenService();
        var inner = new RecordingExecutor();
        var executor = CreateExecutor(tokenService, new CapabilityEnforcingToolExecutor(tokenService, inner));

        var result = executor.Execute("run-1", CreateTwoStepPlan());

        Assert.Equal("succeeded", result.Status);
        Assert.Equal(["run-1:planner-step:1", "run-1:planner-step:2"], inner.Requests.Select(item => item.InvocationId));
        Assert.Equal(["echo.message", "echo.message"], inner.Requests.Select(item => item.Action));
        Assert.All(inner.Requests, request => Assert.Equal(string.Empty, request.CapabilityToken));
        Assert.Equal("{\"message\":\"first\"}", inner.Requests[0].InputJson);
        Assert.Equal("{\"message\":\"second\"}", inner.Requests[1].InputJson);
        Assert.All(result.Steps, step => Assert.Equal("allow", step.ToolResult.CapabilityDecision!.Decision));
    }

    [Theory]
    [InlineData("sandbox_timeout")]
    [InlineData("sandbox_protocol_error")]
    public void Execute_WhenToolFails_StopsAndPropagatesStableError(string error)
    {
        var tokenService = CapabilityTestData.CreateTokenService();
        var inner = new FixedFailureExecutor(error);
        var executor = CreateExecutor(tokenService, new CapabilityEnforcingToolExecutor(tokenService, inner));

        var result = executor.Execute("run-1", CreateTwoStepPlan());

        Assert.Equal("failed", result.Status);
        Assert.Equal(error, result.ErrorCode);
        Assert.Single(result.Steps);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public void Execute_WhenCapabilityIsDenied_DoesNotReachToolAndStopsPlan()
    {
        var validator = CapabilityTestData.CreateTokenService();
        var inner = new RecordingExecutor();
        var executor = CreateExecutor(
            new FixedTokenIssuer("malformed"),
            new CapabilityEnforcingToolExecutor(validator, inner));

        var result = executor.Execute("run-1", CreateTwoStepPlan());

        Assert.Equal("failed", result.Status);
        Assert.Equal("capability_denied", result.ErrorCode);
        Assert.Single(result.Steps);
        Assert.Empty(inner.Requests);
        Assert.Equal("deny", result.Steps[0].ToolResult.CapabilityDecision!.Decision);
    }

    [Fact]
    public void Execute_WhenAlreadyCancelled_DoesNotIssueOrExecuteAnyStep()
    {
        var tokenService = CapabilityTestData.CreateTokenService();
        var inner = new RecordingExecutor();
        var executor = CreateExecutor(tokenService, new CapabilityEnforcingToolExecutor(tokenService, inner));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = executor.Execute("run-1", CreateTwoStepPlan(), cancellation.Token);

        Assert.Equal("cancelled", result.Status);
        Assert.Equal("execution_cancelled", result.ErrorCode);
        Assert.Empty(result.Steps);
        Assert.Empty(inner.Requests);
    }

    private static PlannerStepExecutor CreateExecutor(
        ICapabilityTokenIssuer issuer,
        IToolExecutor executor) =>
        new(
            new AllowedActionCatalog([CreateAction()]),
            issuer,
            executor,
            new FixedTimeSource(
                Instant,
                new TimeSourceInfo("record", "test", "clock", "utc-millis", null)));

    private static AllowedActionDefinition CreateAction() =>
        new(
            "echo.message",
            new ToolRef("echo", "1.0"),
            [new AllowedActionArgumentDefinition("message", true)]);

    private static PlannerPlan CreateTwoStepPlan() =>
        new(
            PlannerSchemaVersions.CurrentPlanVersion,
            "plan-1",
            "workflow.plan",
            [
                new PlannerPlanStep(1, "step-1", "echo.message", new Dictionary<string, string> { ["message"] = "first" }),
                new PlannerPlanStep(2, "step-2", "echo.message", new Dictionary<string, string> { ["message"] = "second" })
            ]);

    private sealed class RecordingExecutor : IToolExecutor
    {
        public List<ToolExecutionRequest> Requests { get; } = [];
        public ToolExecutionResult Execute(ToolExecutionRequest request)
        {
            Requests.Add(request);
            return new ToolExecutionResult(
                request.InvocationId,
                request.Tool,
                "succeeded",
                request.InputJson,
                request.InputJson,
                null);
        }
    }

    private sealed class FixedFailureExecutor : IToolExecutor
    {
        private readonly string _error;
        public FixedFailureExecutor(string error) => _error = error;
        public int CallCount { get; private set; }
        public ToolExecutionResult Execute(ToolExecutionRequest request)
        {
            CallCount++;
            return new ToolExecutionResult(
                request.InvocationId,
                request.Tool,
                "failed",
                request.InputJson,
                "{}",
                _error);
        }
    }

    private sealed class FixedTokenIssuer : ICapabilityTokenIssuer
    {
        private readonly string _token;
        public FixedTokenIssuer(string token) => _token = token;
        public string Issue(ToolCapabilityScope scope, DateTimeOffset issuedAtUtc) => _token;
    }
}
