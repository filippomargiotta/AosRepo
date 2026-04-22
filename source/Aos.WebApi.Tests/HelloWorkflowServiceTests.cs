using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Aos.WebApi.Services;
using Microsoft.Extensions.Options;
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
        Assert.Equal(SchemaVersions.CurrentEventLogSchemaVersion, artifacts.EventLogRecords.Single().SchemaVersion);
        Assert.Equal(1, artifacts.Manifest.EventLog.RecordCount);
        Assert.Equal(artifacts.EventLogRecords.Single().Integrity.ChainMac, artifacts.Manifest.EventLog.LastChainMac);
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
            CreateIntegrityChain(),
            CreateManifestSigner());

        var ex = Assert.Throws<InvalidOperationException>(() => service.CreateHelloArtifacts("run-no-route"));

        Assert.Contains("Router did not select a model", ex.Message);
    }

    private static HelloWorkflowService CreateService(HelloWorkflowOptions options)
    {
        return new HelloWorkflowService(
            new FixedSeedProvider(new SeedInfo("seed-fixed", "test", 1, "test")),
            new FixedTimeSource(
                new DateTimeOffset(2026, 2, 26, 20, 30, 0, TimeSpan.Zero),
                new TimeSourceInfo("record", "stub", "clock-1", "utc-millis", null)),
            Microsoft.Extensions.Options.Options.Create(options),
            CreateRouterService(),
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
            ? new RouterModelCandidate(
                ModelId: "openai-gpt-4.1-mini",
                Provider: "openai",
                Version: "2026-02",
                LatencyMs: 180,
                CostPer1KTokens: 0.4m,
                QualityScore: 82,
                ComplianceScore: 90,
                ComplianceTags: [ "eu", "standard" ])
            : null;

        var rankedCandidates = selectedCandidate is null
            ? Array.Empty<RouterCandidateScore>()
            : [new RouterCandidateScore(selectedCandidate, 0.8125m)];

        return new RouterSelectionResult(
            TaskClass: "workflow.hello",
            Policy: new RouterSelectionPolicy(
                PolicyId: "test-policy",
                EffectiveConstraints: new RouterSelectionConstraints(220, 0.5m, 60, [ "eu", "standard" ]),
                EffectiveWeights: new RouterSelectionWeights(0.35m, 0.2m, 0.3m, 0.15m)),
            SelectedCandidate: selectedCandidate,
            RankedCandidates: rankedCandidates,
            RejectionReasons: []);
    }

    private static IEventLogIntegrityChain CreateIntegrityChain() =>
        new HmacEventLogIntegrityChain(TestHmacKey, TestHmacKeyId);

    private static IManifestSigner CreateManifestSigner() =>
        new HmacManifestSigner(TestHmacKey, TestHmacKeyId);

    private sealed class FixedSeedProvider : ISeedProvider
    {
        private readonly SeedInfo _seed;

        public FixedSeedProvider(SeedInfo seed)
        {
            _seed = seed;
        }

        public SeedInfo GetLockedSeed(string runId) => _seed with { SeedId = $"seed-{runId}" };
    }

    private sealed class FixedTimeSource : ITimeSource
    {
        private readonly DateTimeOffset _instant;
        private readonly TimeSourceInfo _descriptor;

        public FixedTimeSource(DateTimeOffset instant, TimeSourceInfo descriptor)
        {
            _instant = instant;
            _descriptor = descriptor;
        }

        public DateTimeOffset NowUtc() => _instant;

        public TimeSourceInfo Describe() => _descriptor;
    }

    private sealed class FixedRouterService : IRouterService
    {
        private readonly RouterSelectionResult _routingResult;

        public FixedRouterService(RouterSelectionResult routingResult)
        {
            _routingResult = routingResult;
        }

        public RouterSelectionResult SelectModel(RouterSelectionRequest request) => _routingResult;
    }
}
