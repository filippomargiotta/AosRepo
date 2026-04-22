using Aos.WebApi.Models;
using Xunit;

namespace Aos.WebApi.Tests;

public sealed class ManifestValidatorTests
{
    [Fact]
    public void ValidManifest_PassesValidation()
    {
        var now = DateTimeOffset.UtcNow;
        var manifest = new Manifest(
            ManifestVersion: SchemaVersions.CurrentManifestVersion,
            RunId: "run-1",
            Seed: new SeedInfo(
                SeedId: "seed-1",
                Algorithm: "xoroshiro128**",
                Value: 123,
                Derivation: "static test"),
            TimeSource: new TimeSourceInfo(
                Mode: "record",
                Source: "system-utc",
                ClockId: "clock-1",
                Precision: "utc-millis",
                Notes: null),
            Models: new[] { new ModelRef("model-1", "local", "0.0") },
            Tools: new[] { new ToolRef("tool-1", "0.0") },
            PolicyDecisions: new[] { new PolicyDecision("policy-1", "allow", null) },
            RoutingDecisions: new[] { CreateRoutingDecision() },
            EventLog: new EventLogSummary(SchemaVersions.CurrentEventLogSchemaVersion, 1, "chain-mac-1"),
            StartedAtUtc: now,
            CompletedAtUtc: now);

        var errors = ManifestValidator.Validate(manifest);

        Assert.Empty(errors);
    }

    [Fact]
    public void MissingFields_FailsValidation()
    {
        var manifest = new Manifest(
            ManifestVersion: "",
            RunId: "",
            Seed: new SeedInfo(
                SeedId: "",
                Algorithm: "",
                Value: 0,
                Derivation: null),
            TimeSource: new TimeSourceInfo(
                Mode: "invalid",
                Source: "",
                ClockId: "",
                Precision: "",
                Notes: null),
            Models: Array.Empty<ModelRef>(),
            Tools: Array.Empty<ToolRef>(),
            PolicyDecisions: Array.Empty<PolicyDecision>(),
            RoutingDecisions: Array.Empty<RouterSelectionResult>(),
            EventLog: new EventLogSummary("", 0, ""),
            StartedAtUtc: DateTimeOffset.UtcNow,
            CompletedAtUtc: null);

        var errors = ManifestValidator.Validate(manifest);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void UnsupportedManifestVersion_FailsValidation()
    {
        var now = DateTimeOffset.UtcNow;
        var manifest = new Manifest(
            ManifestVersion: "0.1",
            RunId: "run-1",
            Seed: new SeedInfo("seed-1", "xoroshiro128**", 123, "static test"),
            TimeSource: new TimeSourceInfo("record", "system-utc", "clock-1", "utc-millis", null),
            Models: new[] { new ModelRef("model-1", "local", "0.0") },
            Tools: new[] { new ToolRef("tool-1", "0.0") },
            PolicyDecisions: new[] { new PolicyDecision("policy-1", "allow", null) },
            RoutingDecisions: new[] { CreateRoutingDecision() },
            EventLog: new EventLogSummary(SchemaVersions.CurrentEventLogSchemaVersion, 1, "chain-mac-1"),
            StartedAtUtc: now,
            CompletedAtUtc: now);

        var errors = ManifestValidator.Validate(manifest);

        Assert.Contains(
            "ManifestVersion '0.1' is not supported. Supported version: 0.3.",
            errors);
    }

    [Fact]
    public void MissingNestedReferenceFields_FailsValidation()
    {
        var now = DateTimeOffset.UtcNow;
        var manifest = new Manifest(
            ManifestVersion: SchemaVersions.CurrentManifestVersion,
            RunId: "run-1",
            Seed: new SeedInfo("seed-1", "xoroshiro128**", 123, "static test"),
            TimeSource: new TimeSourceInfo("record", "system-utc", "clock-1", "utc-millis", null),
            Models: new[] { new ModelRef("", "", "") },
            Tools: new[] { new ToolRef("", "") },
            PolicyDecisions: new[] { new PolicyDecision("", "maybe", null) },
            RoutingDecisions: new[]
            {
                CreateRoutingDecision() with
                {
                    TaskClass = "",
                    SelectedCandidate = CreateRoutingDecision().SelectedCandidate! with { ModelId = "", Provider = "", Version = "" },
                    RankedCandidates =
                    [
                        new RouterCandidateScore(
                            CreateRoutingDecision().SelectedCandidate! with { ModelId = "", Provider = "", Version = "" },
                            0.1m)
                    ]
                }
            },
            EventLog: new EventLogSummary("", 0, ""),
            StartedAtUtc: now,
            CompletedAtUtc: now);

        var errors = ManifestValidator.Validate(manifest);

        Assert.Contains("Models[0].ModelId is required.", errors);
        Assert.Contains("Models[0].Provider is required.", errors);
        Assert.Contains("Models[0].Version is required.", errors);
        Assert.Contains("Tools[0].ToolId is required.", errors);
        Assert.Contains("Tools[0].Version is required.", errors);
        Assert.Contains("PolicyDecisions[0].PolicyId is required.", errors);
        Assert.Contains("PolicyDecisions[0].Decision must be 'allow' or 'deny'.", errors);
        Assert.Contains("RoutingDecisions[0].TaskClass is required.", errors);
        Assert.Contains("RoutingDecisions[0].SelectedCandidate.ModelId is required.", errors);
        Assert.Contains("RoutingDecisions[0].SelectedCandidate.Provider is required.", errors);
        Assert.Contains("RoutingDecisions[0].SelectedCandidate.Version is required.", errors);
        Assert.Contains("EventLog.SchemaVersion is required.", errors);
        Assert.Contains("EventLog.RecordCount must be greater than zero.", errors);
        Assert.Contains("EventLog.LastChainMac is required.", errors);
    }

    [Fact]
    public void MissingRoutingDecision_FailsValidation()
    {
        var now = DateTimeOffset.UtcNow;
        var manifest = new Manifest(
            ManifestVersion: SchemaVersions.CurrentManifestVersion,
            RunId: "run-1",
            Seed: new SeedInfo("seed-1", "xoroshiro128**", 123, "static test"),
            TimeSource: new TimeSourceInfo("record", "system-utc", "clock-1", "utc-millis", null),
            Models: new[] { new ModelRef("model-1", "local", "0.0") },
            Tools: new[] { new ToolRef("tool-1", "0.0") },
            PolicyDecisions: new[] { new PolicyDecision("policy-1", "allow", null) },
            RoutingDecisions: Array.Empty<RouterSelectionResult>(),
            EventLog: new EventLogSummary(SchemaVersions.CurrentEventLogSchemaVersion, 1, "chain-mac-1"),
            StartedAtUtc: now,
            CompletedAtUtc: now);

        var errors = ManifestValidator.Validate(manifest);

        Assert.Contains("At least one RoutingDecision is required.", errors);
    }

    private static RouterSelectionResult CreateRoutingDecision()
    {
        var candidate = new RouterModelCandidate(
            ModelId: "model-1",
            Provider: "local",
            Version: "0.0",
            LatencyMs: 10,
            CostPer1KTokens: 0.01m,
            QualityScore: 80,
            ComplianceScore: 90,
            ComplianceTags: [ "standard" ]);

        return new RouterSelectionResult(
            TaskClass: "workflow.hello",
            Policy: new RouterSelectionPolicy(
                PolicyId: "test-policy",
                EffectiveConstraints: new RouterSelectionConstraints(100, 0.1m, 70, [ "standard" ]),
                EffectiveWeights: new RouterSelectionWeights(0.25m, 0.25m, 0.25m, 0.25m)),
            SelectedCandidate: candidate,
            RankedCandidates: [new RouterCandidateScore(candidate, 0.9m)],
            RejectionReasons: []);
    }
}
