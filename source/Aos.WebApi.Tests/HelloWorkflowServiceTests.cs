using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Aos.WebApi.Services;
using System.Text.Json;
using Xunit;

namespace Aos.WebApi.Tests;

public sealed class HelloWorkflowServiceTests
{
    private const string TestHmacKey = "test-hmac-key";
    private const string TestHmacKeyId = "test-key";

    [Fact]
    public void CreateHelloArtifacts_UsesRouterSelectedModelAndConfiguredToolsAndPolicies()
    {
        var capabilityTokenService = CapabilityTestData.CreateTokenService();
        var service = new HelloWorkflowService(
            new FixedSeedProvider(new SeedInfo("seed-run-1", "test", 123, "test")),
            new FixedTimeSource(
                new DateTimeOffset(2026, 2, 26, 20, 0, 0, TimeSpan.Zero),
                new TimeSourceInfo("record", "stub", "clock-1", "utc-millis", null)),
            Microsoft.Extensions.Options.Options.Create(new HelloWorkflowOptions
            {
                Routing = new HelloWorkflowRoutingOptions
                {
                    TaskClass = "workflow.hello",
                    MaxLatencyMs = 220,
                    MaxCostPer1KTokens = 0.5m,
                    MinQualityScore = 60,
                    RequiredComplianceTags = [ "eu", "standard" ]
                },
                Models =
                [
                    new HelloWorkflowModelOptions
                    {
                        ModelId = "unused-configured-model",
                        Provider = "openai",
                        Version = "unused"
                    }
                ],
                Tools =
                [
                    new HelloWorkflowToolOptions
                    {
                        ToolId = "web-search",
                        Version = "1.0"
                    }
                ],
                PolicyDecisions =
                [
                    new HelloWorkflowPolicyOptions
                    {
                        PolicyId = "allow-approved-tools",
                        Decision = "allow",
                        Reason = "configured default policy"
                    }
                ]
            }),
            CreateRouterService(),
            capabilityTokenService,
            CapabilityTestData.CreateEnforcingExecutor(capabilityTokenService),
            CreateIntegrityChain(),
            CreateManifestSigner());

        var artifacts = service.CreateHelloArtifacts("run-1");

        Assert.Equal(new[] { new ModelRef("openai-gpt-4.1-mini", "openai", "2026-02") }, artifacts.Manifest.Models);
        Assert.Single(artifacts.Manifest.RoutingDecisions);
        Assert.Equal("workflow.hello", artifacts.Manifest.RoutingDecisions.Single().TaskClass);
        Assert.Equal("openai-gpt-4.1-mini", artifacts.Manifest.RoutingDecisions.Single().SelectedCandidate!.ModelId);
        Assert.Equal(new[] { new ToolRef("web-search", "1.0") }, artifacts.Manifest.Tools);
        Assert.Equal(
            new[]
            {
                new PolicyDecision("allow-approved-tools", "allow", "configured default policy"),
                new PolicyDecision("capability-token-v1", "allow", "capability_valid")
            },
            artifacts.Manifest.PolicyDecisions);
        Assert.Equal(2, artifacts.EventLogRecords.Count);
        Assert.All(
            artifacts.EventLogRecords,
            record => Assert.Equal(SchemaVersions.CurrentEventLogSchemaVersion, record.SchemaVersion));
        Assert.Equal("tool.execution", artifacts.EventLogRecords[0].Entry.EventType);
        Assert.Equal("workflow.hello", artifacts.EventLogRecords[1].Entry.EventType);
        var toolEvent = Assert.IsType<ToolExecutionEvent>(artifacts.EventLogRecords[0].Entry.Data);
        Assert.Equal("run-1:hello-tool:0", toolEvent.InvocationId);
        Assert.Equal("web-search", toolEvent.ToolId);
        Assert.Equal("1.0", toolEvent.ToolVersion);
        Assert.Equal("succeeded", toolEvent.Status);
        Assert.Equal(toolEvent.InputJson, toolEvent.OutputJson);
        Assert.Equal("allow", toolEvent.CapabilityDecision.Decision);
        Assert.Equal("capability_valid", toolEvent.CapabilityDecision.ReasonCode);
        Assert.Equal("cap:run-1:hello-tool:0", toolEvent.CapabilityDecision.TokenId);
        Assert.Equal(2, artifacts.Manifest.EventLog.RecordCount);
        Assert.Equal(artifacts.EventLogRecords[^1].Integrity.ChainMac, artifacts.Manifest.EventLog.LastChainMac);
        Assert.True(CreateManifestSigner().TryValidateRecord(artifacts.ManifestRecord, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void CreateHelloArtifacts_PreservesConfiguredOrderForDeterministicManifestLists()
    {
        var service = CreateService(new HelloWorkflowOptions
        {
            Models =
            [
                new HelloWorkflowModelOptions { ModelId = "model-b", Provider = "p", Version = "2" },
                new HelloWorkflowModelOptions { ModelId = "model-a", Provider = "p", Version = "1" }
            ],
            Tools =
            [
                new HelloWorkflowToolOptions { ToolId = "tool-2", Version = "2" },
                new HelloWorkflowToolOptions { ToolId = "tool-1", Version = "1" }
            ],
            PolicyDecisions =
            [
                new HelloWorkflowPolicyOptions { PolicyId = "policy-z", Decision = "deny", Reason = null },
                new HelloWorkflowPolicyOptions { PolicyId = "policy-a", Decision = "allow", Reason = null }
            ]
        });

        var artifacts = service.CreateHelloArtifacts("run-ordered");

        Assert.Equal(["openai-gpt-4.1-mini"], artifacts.Manifest.Models.Select(m => m.ModelId));
        Assert.Equal(["tool-2", "tool-1"], artifacts.Manifest.Tools.Select(t => t.ToolId));
        Assert.Equal(
            ["policy-z", "policy-a", HmacJwtCapabilityTokenService.PolicyId],
            artifacts.Manifest.PolicyDecisions.Select(p => p.PolicyId));
    }

    [Fact]
    public void CreateHelloArtifacts_WhenToolOutputChanges_ChangesCapturedToolEvent()
    {
        var options = CreateValidOptions();
        var baseline = CreateService(options, new DeterministicEchoToolExecutor())
            .CreateHelloArtifacts("run-tool-drift");
        var changed = CreateService(options, new FixedOutputToolExecutor("{\"message\":\"HELLO-MISMATCH\"}"))
            .CreateHelloArtifacts("run-tool-drift");

        var baselineToolEvent = Assert.IsType<ToolExecutionEvent>(baseline.EventLogRecords[0].Entry.Data);
        var changedToolEvent = Assert.IsType<ToolExecutionEvent>(changed.EventLogRecords[0].Entry.Data);

        Assert.Equal("{\"message\":\"hello\",\"runId\":\"run-tool-drift\"}", baselineToolEvent.OutputJson);
        Assert.Equal("{\"message\":\"HELLO-MISMATCH\"}", changedToolEvent.OutputJson);
        Assert.NotEqual(
            baseline.EventLogRecords[0].Integrity.ChainMac,
            changed.EventLogRecords[0].Integrity.ChainMac);
    }

    [Fact]
    public void CreateHelloArtifacts_WithInvalidCapability_RecordsDenialWithoutExecutingTool()
    {
        const string rawToken = "raw-capability-token-must-not-be-persisted";
        var innerExecutor = new TrackingToolExecutor();
        var tokenService = CapabilityTestData.CreateTokenService();
        var service = CreateService(
            CreateValidOptions(),
            innerExecutor,
            new FixedCapabilityTokenIssuer(rawToken),
            tokenService);

        var artifacts = service.CreateHelloArtifacts("run-denied");

        var toolEvent = Assert.IsType<ToolExecutionEvent>(artifacts.EventLogRecords[0].Entry.Data);
        Assert.Equal("denied", toolEvent.Status);
        Assert.Equal("deny", toolEvent.CapabilityDecision.Decision);
        Assert.Equal("malformed_token", toolEvent.CapabilityDecision.ReasonCode);
        Assert.Equal(0, innerExecutor.CallCount);
        Assert.Equal(
            new PolicyDecision("capability-token-v1", "deny", "malformed_token"),
            artifacts.Manifest.PolicyDecisions[^1]);

        var artifactJson = JsonSerializer.Serialize(new
        {
            artifacts.ManifestRecord,
            artifacts.EventLogRecords
        });
        Assert.DoesNotContain(rawToken, artifactJson, StringComparison.Ordinal);
        Assert.True(CreateManifestSigner().TryValidateRecord(artifacts.ManifestRecord, out _));
        Assert.True(CreateIntegrityChain().TryValidateRecords(artifacts.EventLogRecords, out _));
    }

    [Fact]
    public void CreateHelloArtifacts_WithValidCapability_DoesNotPersistRawToken()
    {
        var tokenService = CapabilityTestData.CreateTokenService();
        var capturingIssuer = new CapturingCapabilityTokenIssuer(tokenService);
        var service = CreateService(
            CreateValidOptions(),
            capabilityTokenIssuer: capturingIssuer,
            capabilityTokenValidator: tokenService);

        var artifacts = service.CreateHelloArtifacts("run-token-redaction");

        Assert.NotNull(capturingIssuer.LastToken);
        var artifactJson = JsonSerializer.Serialize(new
        {
            artifacts.ManifestRecord,
            artifacts.EventLogRecords
        });
        Assert.DoesNotContain(capturingIssuer.LastToken, artifactJson, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateHelloArtifacts_WhenRoutingConfigIsMissingRequiredEntries_Throws()
    {
        var service = CreateService(new HelloWorkflowOptions
        {
            Routing = new HelloWorkflowRoutingOptions { TaskClass = "" },
            Models =
            [
                new HelloWorkflowModelOptions { ModelId = "openai-gpt-4.1-mini", Provider = "openai", Version = "2026-02" }
            ],
            Tools =
            [
                new HelloWorkflowToolOptions { ToolId = "web-search", Version = "1.0" }
            ],
            PolicyDecisions =
            [
                new HelloWorkflowPolicyOptions { PolicyId = "policy-1", Decision = "allow", Reason = null }
            ]
        });

        var ex = Assert.Throws<InvalidOperationException>(() => service.CreateHelloArtifacts("run-invalid"));

        Assert.Contains("HelloWorkflow.Routing.TaskClass is required.", ex.Message);
    }

    [Fact]
    public void CreateHelloArtifacts_WhenRouterHasNoSelection_Throws()
    {
        var capabilityTokenService = CapabilityTestData.CreateTokenService();
        var service = new HelloWorkflowService(
            new FixedSeedProvider(new SeedInfo("seed-run-1", "test", 123, "test")),
            new FixedTimeSource(
                new DateTimeOffset(2026, 2, 26, 20, 0, 0, TimeSpan.Zero),
                new TimeSourceInfo("record", "stub", "clock-1", "utc-millis", null)),
            Microsoft.Extensions.Options.Options.Create(CreateValidOptions()),
            new FixedRouterService(CreateRoutingResult(hasSelection: false)),
            capabilityTokenService,
            CapabilityTestData.CreateEnforcingExecutor(capabilityTokenService),
            CreateIntegrityChain(),
            CreateManifestSigner());

        var ex = Assert.Throws<InvalidOperationException>(() => service.CreateHelloArtifacts("run-no-route"));

        Assert.Contains("Router did not select a model", ex.Message);
    }

    private static HelloWorkflowService CreateService(
        HelloWorkflowOptions options,
        IToolExecutor? toolExecutor = null,
        ICapabilityTokenIssuer? capabilityTokenIssuer = null,
        ICapabilityTokenValidator? capabilityTokenValidator = null)
    {
        var capabilityTokenService = CapabilityTestData.CreateTokenService();
        return new HelloWorkflowService(
            new FixedSeedProvider(new SeedInfo("seed-fixed", "test", 1, "test")),
            new FixedTimeSource(
                new DateTimeOffset(2026, 2, 26, 20, 30, 0, TimeSpan.Zero),
                new TimeSourceInfo("record", "stub", "clock-1", "utc-millis", null)),
            Microsoft.Extensions.Options.Options.Create(options),
            CreateRouterService(),
            capabilityTokenIssuer ?? capabilityTokenService,
            new CapabilityEnforcingToolExecutor(
                capabilityTokenValidator ?? capabilityTokenService,
                toolExecutor ?? new DeterministicEchoToolExecutor()),
            CreateIntegrityChain(),
            CreateManifestSigner());
    }

    private static HelloWorkflowOptions CreateValidOptions() => new()
    {
        Tools =
        [
            new HelloWorkflowToolOptions { ToolId = "web-search", Version = "1.0" }
        ],
        PolicyDecisions =
        [
            new HelloWorkflowPolicyOptions { PolicyId = "policy-1", Decision = "allow", Reason = null }
        ]
    };

    private static IRouterService CreateRouterService() => new FixedRouterService(CreateRoutingResult());

    private static RouterSelectionResult CreateRoutingResult(bool hasSelection = true)
    {
        var selectedCandidate = hasSelection
            ? RouterTestData.CreateCandidate(
                modelId: "openai-gpt-4.1-mini",
                provider: "openai",
                version: "2026-02",
                latencyMs: 180,
                costPer1KTokens: 0.4m,
                qualityScore: 82,
                complianceTags: [ "eu", "standard" ])
            : null;

        return RouterTestData.CreateRoutingDecision(
            taskClass: "workflow.hello",
            policyId: "test-policy",
            candidate: selectedCandidate,
            maxLatencyMs: 220,
            maxCostPer1KTokens: 0.5m,
            minQualityScore: 60,
            requiredComplianceTags: [ "eu", "standard" ],
            effectiveWeights: new RouterSelectionWeights(0.35m, 0.2m, 0.3m, 0.15m),
            score: 0.8125m,
            includeSelection: hasSelection);
    }

    private static IEventLogIntegrityChain CreateIntegrityChain() =>
        new HmacEventLogIntegrityChain(TestHmacKey, TestHmacKeyId);

    private static IManifestSigner CreateManifestSigner() =>
        new HmacManifestSigner(TestHmacKey, TestHmacKeyId);

    private sealed class FixedOutputToolExecutor : IToolExecutor
    {
        private readonly string _outputJson;

        public FixedOutputToolExecutor(string outputJson)
        {
            _outputJson = outputJson;
        }

        public ToolExecutionResult Execute(ToolExecutionRequest request)
        {
            return new ToolExecutionResult(
                InvocationId: request.InvocationId,
                Tool: request.Tool,
                Status: "succeeded",
                InputJson: request.InputJson,
                OutputJson: _outputJson,
                Error: null);
        }
    }

    private sealed class TrackingToolExecutor : IToolExecutor
    {
        public int CallCount { get; private set; }

        public ToolExecutionResult Execute(ToolExecutionRequest request)
        {
            CallCount++;
            return new ToolExecutionResult(
                request.InvocationId,
                request.Tool,
                "succeeded",
                request.InputJson,
                request.InputJson,
                null);
        }
    }

    private sealed class FixedCapabilityTokenIssuer : ICapabilityTokenIssuer
    {
        private readonly string _token;

        public FixedCapabilityTokenIssuer(string token)
        {
            _token = token;
        }

        public string Issue(ToolCapabilityScope scope, DateTimeOffset issuedAtUtc) => _token;
    }

    private sealed class CapturingCapabilityTokenIssuer : ICapabilityTokenIssuer
    {
        private readonly ICapabilityTokenIssuer _inner;

        public CapturingCapabilityTokenIssuer(ICapabilityTokenIssuer inner)
        {
            _inner = inner;
        }

        public string? LastToken { get; private set; }

        public string Issue(ToolCapabilityScope scope, DateTimeOffset issuedAtUtc)
        {
            LastToken = _inner.Issue(scope, issuedAtUtc);
            return LastToken;
        }
    }
}
