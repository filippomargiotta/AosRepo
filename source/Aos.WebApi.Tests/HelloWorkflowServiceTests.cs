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
    public void CreateHelloArtifacts_UsesConfiguredModelsToolsAndPolicies()
    {
        var service = new HelloWorkflowService(
            new FixedSeedProvider(new SeedInfo("seed-run-1", "test", 123, "test")),
            new FixedTimeSource(
                new DateTimeOffset(2026, 2, 26, 20, 0, 0, TimeSpan.Zero),
                new TimeSourceInfo("record", "stub", "clock-1", "utc-millis", null)),
            Microsoft.Extensions.Options.Options.Create(new HelloWorkflowOptions
            {
                Models =
                [
                    new HelloWorkflowModelOptions
                    {
                        ModelId = "openai-gpt-4.1-mini",
                        Provider = "openai",
                        Version = "2026-02"
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
            CreateIntegrityChain());

        var artifacts = service.CreateHelloArtifacts("run-1");

        Assert.Equal(new[] { new ModelRef("openai-gpt-4.1-mini", "openai", "2026-02") }, artifacts.Manifest.Models);
        Assert.Equal(new[] { new ToolRef("web-search", "1.0") }, artifacts.Manifest.Tools);
        Assert.Equal(
            new[] { new PolicyDecision("allow-approved-tools", "allow", "configured default policy") },
            artifacts.Manifest.PolicyDecisions);
        Assert.Equal(SchemaVersions.CurrentEventLogSchemaVersion, artifacts.EventLogRecords.Single().SchemaVersion);
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

        Assert.Equal(["model-b", "model-a"], artifacts.Manifest.Models.Select(m => m.ModelId));
        Assert.Equal(["tool-2", "tool-1"], artifacts.Manifest.Tools.Select(t => t.ToolId));
        Assert.Equal(["policy-z", "policy-a"], artifacts.Manifest.PolicyDecisions.Select(p => p.PolicyId));
    }

    [Fact]
    public void CreateHelloArtifacts_WhenConfigIsMissingRequiredEntries_Throws()
    {
        var service = CreateService(new HelloWorkflowOptions
        {
            Models =
            [
                new HelloWorkflowModelOptions { ModelId = "", Provider = "openai", Version = "2026-02" }
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

        Assert.Contains("HelloWorkflow.Models[].ModelId is required.", ex.Message);
    }

    private static HelloWorkflowService CreateService(HelloWorkflowOptions options)
    {
        return new HelloWorkflowService(
            new FixedSeedProvider(new SeedInfo("seed-fixed", "test", 1, "test")),
            new FixedTimeSource(
                new DateTimeOffset(2026, 2, 26, 20, 30, 0, TimeSpan.Zero),
                new TimeSourceInfo("record", "stub", "clock-1", "utc-millis", null)),
            Microsoft.Extensions.Options.Options.Create(options),
            CreateIntegrityChain());
    }

    private static IEventLogIntegrityChain CreateIntegrityChain() =>
        new HmacEventLogIntegrityChain(TestHmacKey, TestHmacKeyId);

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
}
