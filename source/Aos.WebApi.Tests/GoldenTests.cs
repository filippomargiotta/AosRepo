using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Aos.WebApi.Services;
using Xunit;

namespace Aos.WebApi.Tests;

public sealed class GoldenTests
{
    private const string GoldenRunId = "run-golden-hello-1";
    private const string GoldenHmacKey = GoldenArtifactTestSupport.GoldenHmacKey;
    private const string GoldenHmacKeyId = GoldenArtifactTestSupport.GoldenHmacKeyId;
    private static readonly DateTimeOffset GoldenInstant = new(2026, 2, 26, 19, 0, 0, TimeSpan.Zero);
    private static readonly SeedInfo GoldenSeed = new(
        SeedId: $"seed-{GoldenRunId}",
        Algorithm: "test-sequence",
        Value: 424242,
        Derivation: "golden-fixed");

    [Fact]
    public void RecordHelloWorkflow_MatchesCheckedInGoldenArtifacts()
    {
        var recordTimeSource = new RecordingTimeSource(new FixedSequenceTimeSource(
            [GoldenInstant, GoldenInstant, GoldenInstant],
            new TimeSourceInfo(
                Mode: "record",
                Source: "golden-fixed",
                ClockId: "clock-golden-1",
                Precision: "utc-millis",
                Notes: "golden fixture")));

        var service = new HelloWorkflowService(
            new FixedSeedProvider(GoldenSeed, preserveSeedId: true),
            recordTimeSource,
            Microsoft.Extensions.Options.Options.Create(CreateGoldenHelloWorkflowOptions()),
            new FixedRouterService(CreateGoldenRoutingDecision()),
            new DeterministicEchoToolExecutor(),
            CreateIntegrityChain(),
            CreateManifestSigner());

        var artifacts = service.CreateHelloArtifacts(GoldenRunId);

        Assert.Equal([GoldenInstant, GoldenInstant, GoldenInstant], recordTimeSource.GetRecordedInstants());
        Assert.Equal(GoldenArtifactTestSupport.ReadGoldenManifestJson(), GoldenArtifactTestSupport.SerializeManifestRecord(artifacts.ManifestRecord));
        Assert.Equal(GoldenArtifactTestSupport.ReadGoldenEventLogJsonl(), GoldenArtifactTestSupport.SerializeEventLogLines(artifacts.EventLogRecords));
    }

    [Fact]
    public void ReplayHelloWorkflow_FromGoldenArtifacts_ReproducesDeterministicOutput()
    {
        var goldenManifestRecord = GoldenArtifactTestSupport.ReadGoldenManifestRecord();
        var goldenEventLogRecords = GoldenArtifactTestSupport.ReadGoldenEventLogRecords();

        var replayTimeSource = new ReplayTimeSource(
            [
                goldenManifestRecord.Manifest.StartedAtUtc,
                .. goldenEventLogRecords.Select(record => record.Entry.OccurredAtUtc)
            ],
            goldenManifestRecord.Manifest.TimeSource);
        var service = new HelloWorkflowService(
            new FixedSeedProvider(goldenManifestRecord.Manifest.Seed, preserveSeedId: true),
            replayTimeSource,
            Microsoft.Extensions.Options.Options.Create(CreateGoldenHelloWorkflowOptions()),
            new FixedRouterService(goldenManifestRecord.Manifest.RoutingDecisions.Single()),
            new DeterministicEchoToolExecutor(),
            CreateIntegrityChain(),
            CreateManifestSigner());

        var replayed = service.CreateHelloArtifacts(goldenManifestRecord.Manifest.RunId);

        Assert.Equal(
            GoldenArtifactTestSupport.SerializeManifestRecord(goldenManifestRecord),
            GoldenArtifactTestSupport.SerializeManifestRecord(replayed.ManifestRecord));

        Assert.Equal(
            GoldenArtifactTestSupport.SerializeEventLogLines(goldenEventLogRecords),
            GoldenArtifactTestSupport.SerializeEventLogLines(replayed.EventLogRecords));
    }

    private static IEventLogIntegrityChain CreateIntegrityChain() =>
        new HmacEventLogIntegrityChain(GoldenHmacKey, GoldenHmacKeyId);

    private static IManifestSigner CreateManifestSigner() =>
        new HmacManifestSigner(GoldenHmacKey, GoldenHmacKeyId);

    private static HelloWorkflowOptions CreateGoldenHelloWorkflowOptions() => new()
    {
        Routing = new HelloWorkflowRoutingOptions
        {
            TaskClass = "workflow.hello",
            MaxLatencyMs = 100,
            MaxCostPer1KTokens = 0.1m,
            MinQualityScore = 50,
            RequiredComplianceTags = [ "standard" ]
        },
        Models =
        [
            new HelloWorkflowModelOptions
            {
                ModelId = "local-null",
                Provider = "local",
                Version = "0.0"
            }
        ],
        Tools =
        [
            new HelloWorkflowToolOptions
            {
                ToolId = "noop",
                Version = "0.0"
            }
        ],
        PolicyDecisions =
        [
            new HelloWorkflowPolicyOptions
            {
                PolicyId = "policy-allow",
                Decision = "allow",
                Reason = "placeholder"
            }
        ]
    };

    private static RouterSelectionResult CreateGoldenRoutingDecision()
    {
        var candidate = RouterTestData.CreateCandidate(
            modelId: "local-null",
            provider: "local",
            version: "0.0",
            complianceTags: [ "standard" ]);

        return RouterTestData.CreateRoutingDecision(
            taskClass: "workflow.hello",
            policyId: "golden-router-policy",
            candidate: candidate,
            maxLatencyMs: 100,
            maxCostPer1KTokens: 0.1m,
            minQualityScore: 50,
            requiredComplianceTags: [ "standard" ],
            effectiveWeights: new RouterSelectionWeights(0.25m, 0.25m, 0.25m, 0.25m),
            score: 0.85m);
    }
}
