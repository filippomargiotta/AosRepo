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
                Data: new { message = "HELLO-MISMATCH", manifestVersion = "0.1" },
                OccurredAtUtc: new DateTimeOffset(2026, 2, 26, 19, 0, 0, TimeSpan.Zero));
            var json = SerializeSignedEventLogLines([entry]);
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
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(GetGoldenPath("manifest.json")), JsonOptions);

            Assert.NotNull(manifest);

            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(
                    manifest! with { CompletedAtUtc = manifest.CompletedAtUtc?.AddMinutes(1) },
                    JsonOptions));
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
                "Mismatch: Manifest field completedAtUtc differs: expected \"2026-02-26T19:01:00+00:00\", actual \"2026-02-26T19:00:00+00:00\".",
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
                "Artifact compatibility failed: Event log line 1 payload manifestVersion '0.9' does not match manifest version '0.1'.",
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
                {"manifestVersion":"","runId":"run-1","seed":{"seedId":"","algorithm":"","value":0,"derivation":null},"timeSource":{"mode":"invalid","source":"","clockId":"","precision":"","notes":null},"models":[],"tools":[],"policyDecisions":[],"startedAtUtc":"2026-02-26T19:00:00+00:00","completedAtUtc":null}
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
        Assert.Contains("The --workflow, --manifest, --eventlog, and --hmac-key arguments are required.", stderr.ToString());
        Assert.Contains("Usage: aos-replay --workflow <name> --manifest <path> --eventlog <path> --hmac-key <value>", stderr.ToString());
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

    private static string GetGoldenPath(string fileName)
    {
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "Golden",
            "hello-workflow-v1",
            fileName));
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

    private static string SerializeSignedEventLogLines(IReadOnlyList<EventLogEntry> entries)
    {
        var records = new HmacEventLogIntegrityChain(TestHmacKey, TestHmacKeyId).SignEntries(entries);
        return string.Join('\n', records.Select(record => JsonSerializer.Serialize(record, JsonOptions)));
    }
}
