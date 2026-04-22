using Aos.WebApi.Models;
using Aos.WebApi.Services;
using Xunit;

namespace Aos.WebApi.Tests;

public sealed class ManifestSignerTests
{
    [Fact]
    public void SignManifest_ProducesValidRecord()
    {
        var signer = CreateSigner();
        var record = signer.SignManifest(CreateManifest());

        Assert.True(signer.TryValidateRecord(record, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void TryValidateRecord_WhenManifestIsModified_ReturnsFailure()
    {
        var signer = CreateSigner();
        var record = signer.SignManifest(CreateManifest()) with
        {
            Manifest = CreateManifest() with
            {
                CompletedAtUtc = CreateManifest().CompletedAtUtc?.AddMinutes(1)
            }
        };

        Assert.False(signer.TryValidateRecord(record, out var error));
        Assert.Equal("Manifest integrity MAC is invalid.", error);
    }

    private static HmacManifestSigner CreateSigner() => new("test-hmac-key", "test-key");

    private static Manifest CreateManifest()
    {
        var now = new DateTimeOffset(2026, 3, 24, 8, 0, 0, TimeSpan.Zero);
        return new Manifest(
            ManifestVersion: SchemaVersions.CurrentManifestVersion,
            RunId: "run-1",
            Seed: new SeedInfo("seed-1", "test", 123, "test"),
            TimeSource: new TimeSourceInfo("record", "stub", "clock-1", "utc-millis", null),
            Models: [new ModelRef("model-1", "local", "0.0")],
            Tools: [new ToolRef("tool-1", "0.0")],
            PolicyDecisions: [new PolicyDecision("policy-1", "allow", null)],
            RoutingDecisions: [CreateRoutingDecision()],
            EventLog: new EventLogSummary(SchemaVersions.CurrentEventLogSchemaVersion, 1, "chain-mac-1"),
            StartedAtUtc: now,
            CompletedAtUtc: now);
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
