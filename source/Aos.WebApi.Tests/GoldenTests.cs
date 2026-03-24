using System.Text;
using System.Text.Json;
using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Aos.WebApi.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aos.WebApi.Tests;

public sealed class GoldenTests
{
    private const string GoldenScenario = "hello-workflow-v1";
    private const string GoldenRunId = "run-golden-hello-1";
    private const string GoldenHmacKey = "golden-hmac-key";
    private const string GoldenHmacKeyId = "golden-key-1";
    private static readonly DateTimeOffset GoldenInstant = new(2026, 2, 26, 19, 0, 0, TimeSpan.Zero);
    private static readonly SeedInfo GoldenSeed = new(
        SeedId: $"seed-{GoldenRunId}",
        Algorithm: "test-sequence",
        Value: 424242,
        Derivation: "golden-fixed");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void RecordHelloWorkflow_MatchesCheckedInGoldenArtifacts()
    {
        var recordTimeSource = new RecordingTimeSource(new FixedSequenceTimeSource(
            [GoldenInstant],
            new TimeSourceInfo(
                Mode: "record",
                Source: "golden-fixed",
                ClockId: "clock-golden-1",
                Precision: "utc-millis",
                Notes: "golden fixture")));

        var service = new HelloWorkflowService(
            new FixedSeedProvider(GoldenSeed),
            recordTimeSource,
            Microsoft.Extensions.Options.Options.Create(CreateGoldenHelloWorkflowOptions()),
            CreateIntegrityChain());

        var artifacts = service.CreateHelloArtifacts(GoldenRunId);

        Assert.Equal([GoldenInstant], recordTimeSource.GetRecordedInstants());
        Assert.Equal(ReadGoldenManifestJson(), SerializeManifest(artifacts.Manifest));
        Assert.Equal(ReadGoldenEventLogJsonl(), SerializeEventLogLines(artifacts.EventLogRecords));
    }

    [Fact]
    public void ReplayHelloWorkflow_FromGoldenArtifacts_ReproducesDeterministicOutput()
    {
        var goldenManifest = ReadGoldenManifest();
        var goldenEventLogRecords = ReadGoldenEventLogRecords();

        var replayTimeSource = new ReplayTimeSource([goldenManifest.StartedAtUtc]);
        var service = new HelloWorkflowService(
            new FixedSeedProvider(goldenManifest.Seed),
            replayTimeSource,
            Microsoft.Extensions.Options.Options.Create(CreateGoldenHelloWorkflowOptions()),
            CreateIntegrityChain());

        var replayed = service.CreateHelloArtifacts(goldenManifest.RunId);

        Assert.Equal(goldenManifest.ManifestVersion, replayed.Manifest.ManifestVersion);
        Assert.Equal(goldenManifest.RunId, replayed.Manifest.RunId);
        Assert.Equal(goldenManifest.Seed, replayed.Manifest.Seed);
        Assert.Equal(goldenManifest.Models, replayed.Manifest.Models);
        Assert.Equal(goldenManifest.Tools, replayed.Manifest.Tools);
        Assert.Equal(goldenManifest.PolicyDecisions, replayed.Manifest.PolicyDecisions);
        Assert.Equal(goldenManifest.StartedAtUtc, replayed.Manifest.StartedAtUtc);
        Assert.Equal(goldenManifest.CompletedAtUtc, replayed.Manifest.CompletedAtUtc);
        Assert.Equal("replay", replayed.Manifest.TimeSource.Mode);

        Assert.Equal(
            SerializeEventLogLines(goldenEventLogRecords),
            SerializeEventLogLines(replayed.EventLogRecords));
    }

    private static string ReadGoldenManifestJson() => File.ReadAllText(Path.Combine(GetGoldenDir(), "manifest.json"))
        .TrimEnd('\r', '\n');

    private static string ReadGoldenEventLogJsonl() => File.ReadAllText(Path.Combine(GetGoldenDir(), "eventlog.jsonl"));

    private static Manifest ReadGoldenManifest()
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(ReadGoldenManifestJson(), JsonOptions);
        Assert.NotNull(manifest);
        return manifest!;
    }

    private static IReadOnlyList<EventLogRecord> ReadGoldenEventLogRecords()
    {
        var records = new List<EventLogRecord>();
        foreach (var line in ReadGoldenEventLogJsonl()
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var record = JsonSerializer.Deserialize<EventLogRecord>(line, JsonOptions);
            Assert.NotNull(record);
            records.Add(record!);
        }

        return records;
    }

    private static string SerializeManifest(Manifest manifest) => JsonSerializer.Serialize(manifest, JsonOptions);

    private static string SerializeEventLogLines(IEnumerable<EventLogRecord> records)
    {
        var builder = new StringBuilder();
        foreach (var record in records)
        {
            builder.Append(JsonSerializer.Serialize(record, JsonOptions));
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static IEventLogIntegrityChain CreateIntegrityChain() =>
        new HmacEventLogIntegrityChain(GoldenHmacKey, GoldenHmacKeyId);

    private static string GetGoldenDir() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "Golden",
        GoldenScenario));

    private static HelloWorkflowOptions CreateGoldenHelloWorkflowOptions() => new()
    {
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

    private sealed class FixedSeedProvider : ISeedProvider
    {
        private readonly SeedInfo _seed;

        public FixedSeedProvider(SeedInfo seed)
        {
            _seed = seed;
        }

        public SeedInfo GetLockedSeed(string runId)
        {
            if (!string.Equals(runId, GoldenRunId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected run id: {runId}");
            }

            return _seed;
        }
    }

    private sealed class FixedSequenceTimeSource : ITimeSource
    {
        private readonly Queue<DateTimeOffset> _instants;
        private readonly TimeSourceInfo _descriptor;

        public FixedSequenceTimeSource(IEnumerable<DateTimeOffset> instants, TimeSourceInfo descriptor)
        {
            _instants = new Queue<DateTimeOffset>(instants);
            _descriptor = descriptor;
        }

        public DateTimeOffset NowUtc()
        {
            if (!_instants.TryDequeue(out var instant))
            {
                throw new InvalidOperationException("No more golden instants available.");
            }

            return instant;
        }

        public TimeSourceInfo Describe() => _descriptor;
    }
}
