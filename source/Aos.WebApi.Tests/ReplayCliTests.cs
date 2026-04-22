using System.Text.Json;
using Aos.ReplayCli;
using Aos.WebApi.Models;
using Aos.WebApi.Services;
using Xunit;

namespace Aos.WebApi.Tests;

public sealed class ReplayCliTests
{
    private const string TestHmacKey = "golden-hmac-key";
    private const string TestHmacKeyId = "golden-key-1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task RunAsync_WithGoldenArtifacts_ReturnsSuccess()
    {
        var manifestPath = GetGoldenPath("manifest.json");
        var eventLogPath = GetGoldenPath("eventlog.jsonl");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await ReplayCliRunner.RunAsync(
            ["--workflow", "hello", "--manifest", manifestPath, "--eventlog", eventLogPath, "--hmac-key", TestHmacKey],
            stdout,
            stderr,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("Replay verified for run run-golden-hello-1.", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_WithArtifactDirectory_ReturnsSuccess()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await ReplayCliRunner.RunAsync(
            ["--workflow", "hello", "--artifact-dir", GetGoldenDir(), "--hmac-key", TestHmacKey],
            stdout,
            stderr,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("Replay verified for run run-golden-hello-1.", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_WhenEventLogMismatches_ReturnsFailure()
    {
        var tempDir = CreateTempDir();
        try
        {
            var manifestPath = Path.Combine(tempDir, "manifest.json");
            var eventLogPath = Path.Combine(tempDir, "eventlog.jsonl");
            File.Copy(GetGoldenPath("manifest.json"), manifestPath);

            var entry = new EventLogEntry(
                RunId: "run-golden-hello-1",
                EventType: "workflow.hello",
                Data: new { message = "HELLO-MISMATCH", manifestVersion = SchemaVersions.CurrentManifestVersion },
                OccurredAtUtc: new DateTimeOffset(2026, 2, 26, 19, 0, 0, TimeSpan.Zero));
            var records = new HmacEventLogIntegrityChain(TestHmacKey, TestHmacKeyId).SignEntries([entry]);
            var manifest = ReadGoldenManifestRecord().Manifest with
            {
                EventLog = new EventLogSummary(records[0].SchemaVersion, records.Count, records[^1].Integrity.ChainMac)
            };
            await File.WriteAllTextAsync(manifestPath, SerializeSignedManifest(manifest));
            var json = string.Join('\n', records.Select(record => JsonSerializer.Serialize(record, JsonOptions)));
            await File.WriteAllTextAsync(eventLogPath, json + "\n");

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = await ReplayCliRunner.RunAsync(
                ["--workflow", "hello", "--manifest", manifestPath, "--eventlog", eventLogPath, "--hmac-key", TestHmacKey],
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Contains(
                "Mismatch: Event log line 1 field data.message differs: expected \"HELLO-MISMATCH\", actual \"hello\".",
                stderr.ToString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_WhenManifestMismatches_ReturnsFieldLevelFailure()
    {
        var tempDir = CreateTempDir();
        try
        {
            var manifestPath = Path.Combine(tempDir, "manifest.json");
            var eventLogPath = Path.Combine(tempDir, "eventlog.jsonl");
            var manifest = ReadGoldenManifestRecord().Manifest;

            await File.WriteAllTextAsync(
                manifestPath,
                SerializeSignedManifest(
                    manifest with { CompletedAtUtc = manifest.CompletedAtUtc?.AddMinutes(1) }));
            File.Copy(GetGoldenPath("eventlog.jsonl"), eventLogPath);

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = await ReplayCliRunner.RunAsync(
                ["--workflow", "hello", "--manifest", manifestPath, "--eventlog", eventLogPath, "--hmac-key", TestHmacKey],
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Contains(
                "Mismatch: Manifest field manifest.completedAtUtc differs: expected \"2026-02-26T19:01:00+00:00\", actual \"2026-02-26T19:00:00+00:00\".",
                stderr.ToString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_WhenEventLogRunIdDoesNotMatchManifest_ReturnsCompatibilityFailure()
    {
        var tempDir = CreateTempDir();
        try
        {
            var manifestPath = Path.Combine(tempDir, "manifest.json");
            var eventLogPath = Path.Combine(tempDir, "eventlog.jsonl");
            File.Copy(GetGoldenPath("manifest.json"), manifestPath);

            var entry = new EventLogEntry(
                RunId: "run-mismatch",
                EventType: "workflow.hello",
                Data: new { message = "hello", manifestVersion = SchemaVersions.CurrentManifestVersion },
                OccurredAtUtc: new DateTimeOffset(2026, 2, 26, 19, 0, 0, TimeSpan.Zero));
            await File.WriteAllTextAsync(eventLogPath, SerializeSignedEventLogLines([entry]) + "\n");

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = await ReplayCliRunner.RunAsync(
                ["--workflow", "hello", "--manifest", manifestPath, "--eventlog", eventLogPath, "--hmac-key", TestHmacKey],
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Contains(
                "Artifact compatibility failed: Event log line 1 runId 'run-mismatch' does not match manifest runId 'run-golden-hello-1'.",
                stderr.ToString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_WhenEventLogManifestVersionDoesNotMatchManifest_ReturnsCompatibilityFailure()
    {
        var tempDir = CreateTempDir();
        try
        {
            var manifestPath = Path.Combine(tempDir, "manifest.json");
            var eventLogPath = Path.Combine(tempDir, "eventlog.jsonl");
            File.Copy(GetGoldenPath("manifest.json"), manifestPath);

            var entry = new EventLogEntry(
                RunId: "run-golden-hello-1",
                EventType: "workflow.hello",
                Data: new { message = "hello", manifestVersion = "0.9" },
                OccurredAtUtc: new DateTimeOffset(2026, 2, 26, 19, 0, 0, TimeSpan.Zero));
            await File.WriteAllTextAsync(eventLogPath, SerializeSignedEventLogLines([entry]) + "\n");

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = await ReplayCliRunner.RunAsync(
                ["--workflow", "hello", "--manifest", manifestPath, "--eventlog", eventLogPath, "--hmac-key", TestHmacKey],
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Contains(
                "Artifact compatibility failed: Event log line 1 payload manifestVersion '0.9' does not match manifest version '0.3'.",
                stderr.ToString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_WhenFileIsMissing_ReturnsUsageErrorCode()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await ReplayCliRunner.RunAsync(
            ["--workflow", "hello", "--manifest", "/no/such/manifest.json", "--eventlog", "/no/such/eventlog.jsonl", "--hmac-key", TestHmacKey],
            stdout,
            stderr,
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("Could not find", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_WhenManifestIsInvalid_ReturnsFailure()
    {
        var tempDir = CreateTempDir();
        try
        {
            var manifestPath = Path.Combine(tempDir, "manifest.json");
            var eventLogPath = Path.Combine(tempDir, "eventlog.jsonl");

            await File.WriteAllTextAsync(manifestPath, """
                {"manifest":{"manifestVersion":"","runId":"run-1","seed":{"seedId":"","algorithm":"","value":0,"derivation":null},"timeSource":{"mode":"invalid","source":"","clockId":"","precision":"","notes":null},"models":[],"tools":[],"policyDecisions":[],"eventLog":{"schemaVersion":"","recordCount":0,"lastChainMac":""},"startedAtUtc":"2026-02-26T19:00:00+00:00","completedAtUtc":null},"integrity":{"algorithm":"HMAC-SHA256","keyId":"golden-key-1","manifestMac":"bad"}}
                """);
            await File.WriteAllTextAsync(eventLogPath, File.ReadAllText(GetGoldenPath("eventlog.jsonl")));

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = await ReplayCliRunner.RunAsync(
                ["--workflow", "hello", "--manifest", manifestPath, "--eventlog", eventLogPath, "--hmac-key", TestHmacKey],
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Contains("Manifest validation failed:", stderr.ToString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_WhenWorkflowArgumentIsMissing_ReturnsUsageErrorCode()
    {
        var manifestPath = GetGoldenPath("manifest.json");
        var eventLogPath = GetGoldenPath("eventlog.jsonl");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await ReplayCliRunner.RunAsync(
            ["--manifest", manifestPath, "--eventlog", eventLogPath, "--hmac-key", TestHmacKey],
            stdout,
            stderr,
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains(
            "The --workflow, --hmac-key, and either --artifact-dir or both --manifest/--eventlog arguments are required.",
            stderr.ToString());
        Assert.Contains("Usage: aos-replay --workflow <name> --manifest <path> --eventlog <path> --hmac-key <value>", stderr.ToString());
        Assert.Contains("aos-replay --workflow <name> --artifact-dir <path> --hmac-key <value>", stderr.ToString());
        Assert.Contains("Exit codes: 0=verified, 1=integrity/compatibility/mismatch failure, 2=usage/input failure", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_WhenWorkflowIsUnknown_ReturnsUsageErrorCode()
    {
        var manifestPath = GetGoldenPath("manifest.json");
        var eventLogPath = GetGoldenPath("eventlog.jsonl");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await ReplayCliRunner.RunAsync(
            ["--workflow", "unknown", "--manifest", manifestPath, "--eventlog", eventLogPath, "--hmac-key", TestHmacKey],
            stdout,
            stderr,
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("Unknown workflow 'unknown'. Available workflows: hello.", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_WhenEventLogChainMacIsInvalid_ReturnsIntegrityFailure()
    {
        var tempDir = CreateTempDir();
        try
        {
            var manifestPath = Path.Combine(tempDir, "manifest.json");
            var eventLogPath = Path.Combine(tempDir, "eventlog.jsonl");
            File.Copy(GetGoldenPath("manifest.json"), manifestPath);

            var tamperedRecord = ReadGoldenEventLogRecords().Single() with
            {
                Entry = ReadGoldenEventLogRecords().Single().Entry with
                {
                    Data = new { message = "tampered", manifestVersion = SchemaVersions.CurrentManifestVersion }
                }
            };
            await File.WriteAllTextAsync(eventLogPath, JsonSerializer.Serialize(tamperedRecord, JsonOptions) + "\n");

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = await ReplayCliRunner.RunAsync(
                ["--workflow", "hello", "--manifest", manifestPath, "--eventlog", eventLogPath, "--hmac-key", TestHmacKey],
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Contains("Artifact integrity failed: Event log line 1 chainMac is invalid.", stderr.ToString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_WhenManifestSignatureIsInvalid_ReturnsIntegrityFailure()
    {
        var tempDir = CreateTempDir();
        try
        {
            var manifestPath = Path.Combine(tempDir, "manifest.json");
            var eventLogPath = Path.Combine(tempDir, "eventlog.jsonl");
            var manifestRecord = ReadGoldenManifestRecord();
            File.Copy(GetGoldenPath("eventlog.jsonl"), eventLogPath);

            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(
                    manifestRecord with
                    {
                        Manifest = manifestRecord.Manifest with
                        {
                            CompletedAtUtc = manifestRecord.Manifest.CompletedAtUtc?.AddMinutes(1)
                        }
                    },
                    JsonOptions));

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = await ReplayCliRunner.RunAsync(
                ["--workflow", "hello", "--manifest", manifestPath, "--eventlog", eventLogPath, "--hmac-key", TestHmacKey],
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Contains("Artifact integrity failed: Manifest integrity MAC is invalid.", stderr.ToString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_WhenManifestEventLogSummaryDoesNotMatchEventLog_ReturnsCompatibilityFailure()
    {
        var tempDir = CreateTempDir();
        try
        {
            var manifestPath = Path.Combine(tempDir, "manifest.json");
            var eventLogPath = Path.Combine(tempDir, "eventlog.jsonl");
            var manifest = ReadGoldenManifestRecord().Manifest with
            {
                EventLog = new EventLogSummary(
                    SchemaVersions.CurrentEventLogSchemaVersion,
                    99,
                    "mismatch-chain-mac")
            };

            await File.WriteAllTextAsync(manifestPath, SerializeSignedManifest(manifest));
            File.Copy(GetGoldenPath("eventlog.jsonl"), eventLogPath);

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = await ReplayCliRunner.RunAsync(
                ["--workflow", "hello", "--manifest", manifestPath, "--eventlog", eventLogPath, "--hmac-key", TestHmacKey],
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Contains(
                "Artifact compatibility failed: Manifest eventLog recordCount '99' does not match event log line count '1'.",
                stderr.ToString());
            Assert.Contains(
                "Artifact compatibility failed: Manifest eventLog lastChainMac 'mismatch-chain-mac' does not match event log tail chainMac",
                stderr.ToString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string GetGoldenPath(string fileName)
    {
        return Path.Combine(GetGoldenDir(), fileName);
    }

    private static string GetGoldenDir()
    {
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "Golden",
            "hello-workflow-v1"));
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "aos-replaycli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static IReadOnlyList<EventLogRecord> ReadGoldenEventLogRecords()
    {
        var records = new List<EventLogRecord>();
        foreach (var line in File.ReadAllText(GetGoldenPath("eventlog.jsonl"))
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var record = JsonSerializer.Deserialize<EventLogRecord>(line, JsonOptions);
            Assert.NotNull(record);
            records.Add(record!);
        }

        return records;
    }

    private static ManifestRecord ReadGoldenManifestRecord()
    {
        var manifestRecord = JsonSerializer.Deserialize<ManifestRecord>(File.ReadAllText(GetGoldenPath("manifest.json")), JsonOptions);
        Assert.NotNull(manifestRecord);
        return manifestRecord!;
    }

    private static string SerializeSignedManifest(Manifest manifest)
    {
        var record = new HmacManifestSigner(TestHmacKey, TestHmacKeyId).SignManifest(manifest);
        return JsonSerializer.Serialize(record, JsonOptions);
    }

    private static string SerializeSignedEventLogLines(IReadOnlyList<EventLogEntry> entries)
    {
        var records = new HmacEventLogIntegrityChain(TestHmacKey, TestHmacKeyId).SignEntries(entries);
        return string.Join('\n', records.Select(record => JsonSerializer.Serialize(record, JsonOptions)));
    }
}
