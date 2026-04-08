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
            CreateIntegrityChain(),
            CreateManifestSigner());

        var artifacts = service.CreateHelloArtifacts(GoldenRunId);

        Assert.Equal([GoldenInstant], recordTimeSource.GetRecordedInstants());
        Assert.Equal(ReadGoldenManifestJson(), SerializeManifestRecord(artifacts.ManifestRecord));
        Assert.Equal(ReadGoldenEventLogJsonl(), SerializeEventLogLines(artifacts.EventLogRecords));
    }

    [Fact]
    public void ReplayHelloWorkflow_FromGoldenArtifacts_ReproducesDeterministicOutput()
    {
        var goldenManifestRecord = ReadGoldenManifestRecord();
        var goldenEventLogRecords = ReadGoldenEventLogRecords();

        var replayTimeSource = new ReplayTimeSource(
            [goldenManifestRecord.Manifest.StartedAtUtc],
            goldenManifestRecord.Manifest.TimeSource);
        var service = new HelloWorkflowService(
            new FixedSeedProvider(goldenManifestRecord.Manifest.Seed),
            replayTimeSource,
            Microsoft.Extensions.Options.Options.Create(CreateGoldenHelloWorkflowOptions()),
            CreateIntegrityChain(),
            CreateManifestSigner());

        var replayed = service.CreateHelloArtifacts(goldenManifestRecord.Manifest.RunId);

        Assert.Equal(
            SerializeManifestRecord(goldenManifestRecord),
            SerializeManifestRecord(replayed.ManifestRecord));

        Assert.Equal(
            SerializeEventLogLines(goldenEventLogRecords),
            SerializeEventLogLines(replayed.EventLogRecords));
    }

    private static string ReadGoldenManifestJson() => File.ReadAllText(Path.Combine(GetGoldenDir(), "manifest.json"))
        .TrimEnd('\r', '\n');

    private static string ReadGoldenEventLogJsonl() => File.ReadAllText(Path.Combine(GetGoldenDir(), "eventlog.jsonl"));

    private static ManifestRecord ReadGoldenManifestRecord()
    {
        var manifestRecord = JsonSerializer.Deserialize<ManifestRecord>(ReadGoldenManifestJson(), JsonOptions);
        Assert.NotNull(manifestRecord);
        return manifestRecord!;
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

    private static string SerializeManifestRecord(ManifestRecord manifestRecord) =>
        JsonSerializer.Serialize(manifestRecord, JsonOptions);

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

    private static IManifestSigner CreateManifestSigner() =>
        new HmacManifestSigner(GoldenHmacKey, GoldenHmacKeyId);

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
