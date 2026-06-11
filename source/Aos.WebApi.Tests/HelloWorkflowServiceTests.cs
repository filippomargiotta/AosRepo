using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Aos.WebApi.Services;
using Xunit;

namespace Aos.WebApi.Tests;

public sealed class HelloWorkflowServiceTests
{
    private const string TestHmacKey = "test-hmac-key";
    private const string TestHmacKeyId = "test-key";

    [Fact]
    public void CreateHelloArtifacts_UsesRouterSelectedModelAndConfiguredToolsAndPolicies()
    {
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
            new DeterministicEchoToolExecutor(),
            CreateIntegrityChain(),
            CreateManifestSigner());

        var artifacts = service.CreateHelloArtifacts("run-1");

        Assert.Equal(new[] { new ModelRef("openai-gpt-4.1-mini", "openai", "2026-02") }, artifacts.Manifest.Models);
        Assert.Single(artifacts.Manifest.RoutingDecisions);
        Assert.Equal("workflow.hello", artifacts.Manifest.RoutingDecisions.Single().TaskClass);
        Assert.Equal("openai-gpt-4.1-mini", artifacts.Manifest.RoutingDecisions.Single().SelectedCandidate!.ModelId);
        Assert.Equal(new[] { new ToolRef("web-search", "1.0") }, artifacts.Manifest.Tools);
        Assert.Equal(
            new[] { new PolicyDecision("allow-approved-tools", "allow", "configured default policy") },
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
        Assert.Equal(["policy-z", "policy-a"], artifacts.Manifest.PolicyDecisions.Select(p => p.PolicyId));
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
        var service = new HelloWorkflowService(
            new FixedSeedProvider(new SeedInfo("seed-run-1", "test", 123, "test")),
            new FixedTimeSource(
                new DateTimeOffset(2026, 2, 26, 20, 0, 0, TimeSpan.Zero),
                new TimeSourceInfo("record", "stub", "clock-1", "utc-millis", null)),
            Microsoft.Extensions.Options.Options.Create(CreateValidOptions()),
            new FixedRouterService(CreateRoutingResult(hasSelection: false)),
            new DeterministicEchoToolExecutor(),
            CreateIntegrityChain(),
            CreateManifestSigner());

        var ex = Assert.Throws<InvalidOperationException>(() => service.CreateHelloArtifacts("run-no-route"));

        Assert.Contains("Router did not select a model", ex.Message);
    }

    private static HelloWorkflowService CreateService(HelloWorkflowOptions options, IToolExecutor? toolExecutor = null)
    {
        return new HelloWorkflowService(
            new FixedSeedProvider(new SeedInfo("seed-fixed", "test", 1, "test")),
            new FixedTimeSource(
                new DateTimeOffset(2026, 2, 26, 20, 30, 0, TimeSpan.Zero),
                new TimeSourceInfo("record", "stub", "clock-1", "utc-millis", null)),
            Microsoft.Extensions.Options.Options.Create(options),
            CreateRouterService(),
            toolExecutor ?? new DeterministicEchoToolExecutor(),
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
}
