using Aos.WebApi.Models;
using Aos.WebApi.Services;
using Xunit;

namespace Aos.WebApi.Tests;

public sealed class CapabilityTokenTests
{
    private static readonly DateTimeOffset IssuedAt =
        new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Execute_WithValidCapability_DelegatesWithoutExposingToken()
    {
        var tokenService = CapabilityTestData.CreateTokenService();
        var innerExecutor = new TrackingToolExecutor();
        var executor = new CapabilityEnforcingToolExecutor(tokenService, innerExecutor);
        var scope = CreateScope();
        var token = tokenService.Issue(scope, IssuedAt);

        var result = executor.Execute(CreateRequest(scope, token, IssuedAt));

        Assert.Equal("succeeded", result.Status);
        Assert.Equal("allow", result.CapabilityDecision!.Decision);
        Assert.Equal("capability_valid", result.CapabilityDecision.ReasonCode);
        Assert.Equal($"cap:{scope.InvocationId}", result.CapabilityDecision.TokenId);
        Assert.Equal(1, innerExecutor.CallCount);
        Assert.Equal(string.Empty, innerExecutor.LastRequest!.CapabilityToken);
    }

    [Fact]
    public void Execute_WithExpiredCapability_DeniesWithoutCallingDelegate()
    {
        var tokenService = CapabilityTestData.CreateTokenService(lifetimeSeconds: 60);
        var innerExecutor = new TrackingToolExecutor();
        var executor = new CapabilityEnforcingToolExecutor(tokenService, innerExecutor);
        var scope = CreateScope();
        var token = tokenService.Issue(scope, IssuedAt);

        var result = executor.Execute(CreateRequest(scope, token, IssuedAt.AddSeconds(60)));

        Assert.Equal("denied", result.Status);
        Assert.Equal("capability_denied", result.Error);
        Assert.Equal("token_expired", result.CapabilityDecision!.ReasonCode);
        Assert.Equal(0, innerExecutor.CallCount);
    }

    [Theory]
    [InlineData("run", "run_mismatch")]
    [InlineData("invocation", "invocation_mismatch")]
    [InlineData("tool", "tool_mismatch")]
    [InlineData("version", "tool_version_mismatch")]
    [InlineData("action", "action_mismatch")]
    public void Validate_WithMismatchedScope_Denies(string field, string expectedReason)
    {
        var tokenService = CapabilityTestData.CreateTokenService();
        var issuedScope = CreateScope();
        var expectedScope = field switch
        {
            "run" => issuedScope with { RunId = "run-other" },
            "invocation" => issuedScope with { InvocationId = "invocation-other" },
            "tool" => issuedScope with { ToolId = "other-tool" },
            "version" => issuedScope with { ToolVersion = "2.0" },
            "action" => issuedScope with { Action = "tool.admin" },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        var token = tokenService.Issue(issuedScope, IssuedAt);

        var decision = tokenService.Validate(token, expectedScope, IssuedAt);

        Assert.Equal("deny", decision.Decision);
        Assert.Equal(expectedReason, decision.ReasonCode);
    }

    [Fact]
    public void Validate_WithWrongSignature_DeniesWithoutTrustingTokenId()
    {
        var issuer = CapabilityTestData.CreateTokenService(signingKey: "issuer-capability-key-at-least-32-bytes");
        var validator = CapabilityTestData.CreateTokenService(signingKey: "validator-capability-key-at-least-32-bytes");
        var scope = CreateScope();

        var decision = validator.Validate(issuer.Issue(scope, IssuedAt), scope, IssuedAt);

        Assert.Equal("deny", decision.Decision);
        Assert.Equal("invalid_signature", decision.ReasonCode);
        Assert.Null(decision.TokenId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("a.b.c")]
    public void Validate_WithMissingOrMalformedToken_Denies(string token)
    {
        var tokenService = CapabilityTestData.CreateTokenService();

        var decision = tokenService.Validate(token, CreateScope(), IssuedAt);

        Assert.Equal("deny", decision.Decision);
        Assert.Contains(decision.ReasonCode, new[] { "missing_token", "malformed_token" });
    }

    private static ToolCapabilityScope CreateScope() =>
        new("run-1", "run-1:tool:0", "echo", "1.0", "tool.execute");

    private static ToolExecutionRequest CreateRequest(
        ToolCapabilityScope scope,
        string token,
        DateTimeOffset requestedAtUtc) =>
        new(
            RunId: scope.RunId,
            InvocationId: scope.InvocationId,
            Tool: new ToolRef(scope.ToolId, scope.ToolVersion),
            Action: scope.Action,
            InputJson: "{\"message\":\"hello\"}",
            CapabilityToken: token,
            RequestedAtUtc: requestedAtUtc);

    private sealed class TrackingToolExecutor : IToolExecutor
    {
        public int CallCount { get; private set; }
        public ToolExecutionRequest? LastRequest { get; private set; }

        public ToolExecutionResult Execute(ToolExecutionRequest request)
        {
            CallCount++;
            LastRequest = request;
            return new ToolExecutionResult(
                request.InvocationId,
                request.Tool,
                "succeeded",
                request.InputJson,
                request.InputJson,
                null);
        }
    }
}
