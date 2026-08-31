using System.Text.Json;
using Aos.ReplayCli;
using Aos.WebApi.Models;
using Aos.WebApi.Services;
using Xunit;

namespace Aos.WebApi.Tests;

public sealed class ReplayCliTests
{
    private const string TestHmacKey = GoldenArtifactTestSupport.GoldenHmacKey;
    private const string TestHmacKeyId = GoldenArtifactTestSupport.GoldenHmacKeyId;

    [Fact]
    public async Task RunAsync_WithGoldenArtifacts_ReturnsSuccess()
    {
        var manifestPath = GoldenArtifactTestSupport.GetGoldenPath("manifest.json");
        var eventLogPath = GoldenArtifactTestSupport.GetGoldenPath("eventlog.jsonl");
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
            ["--workflow", "hello", "--artifact-dir", GoldenArtifactTestSupport.GetGoldenDir(), "--hmac-key", TestHmacKey],
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
        var tempDir = GoldenArtifactTestSupport.CreateTempDir("aos-replaycli-tests");
        try
        {
            var manifestPath = Path.Combine(tempDir, "manifest.json");
            var eventLogPath = Path.Combine(tempDir, "eventlog.jsonl");
            File.Copy(GoldenArtifactTestSupport.GetGoldenPath("manifest.json"), manifestPath);

            var entries = GoldenArtifactTestSupport.ReadGoldenEventLogRecords()
                .Select(record => record.Entry)
                .ToArray();
            var expectedToolEvent = JsonSerializer.Deserialize<ToolExecutionEvent>(
                JsonSerializer.Serialize(entries[0].Data, GoldenArtifactTestSupport.JsonOptions),
                GoldenArtifactTestSupport.JsonOptions)!;
            entries[0] = entries[0] with
            {
                Data = expectedToolEvent with
                {
                    OutputJson = "{\"message\":\"HELLO-MISMATCH\",\"runId\":\"run-golden-hello-1\"}"
                }
            };
            var records = new HmacEventLogIntegrityChain(TestHmacKey, TestHmacKeyId).SignEntries(entries);
            var manifest = GoldenArtifactTestSupport.ReadGoldenManifestRecord().Manifest with
            {
                EventLog = new EventLogSummary(records[0].SchemaVersion, records.Count, records[^1].Integrity.ChainMac)
            };
            await File.WriteAllTextAsync(
                manifestPath,
                GoldenArtifactTestSupport.SerializeSignedManifest(manifest, TestHmacKey, TestHmacKeyId));
            var json = string.Join('\n', records.Select(record =>
                JsonSerializer.Serialize(record, GoldenArtifactTestSupport.JsonOptions)));
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
                "Mismatch: Manifest field manifest.eventLog.lastChainMac differs:",
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
        var tempDir = GoldenArtifactTestSupport.CreateTempDir("aos-replaycli-tests");
        try
        {
            var manifestPath = Path.Combine(tempDir, "manifest.json");
            var eventLogPath = Path.Combine(tempDir, "eventlog.jsonl");
            var manifest = GoldenArtifactTestSupport.ReadGoldenManifestRecord().Manifest;

            await File.WriteAllTextAsync(
                manifestPath,
                GoldenArtifactTestSupport.SerializeSignedManifest(
                    manifest with { CompletedAtUtc = manifest.CompletedAtUtc?.AddMinutes(1) },
                    TestHmacKey,
                    TestHmacKeyId));
            File.Copy(GoldenArtifactTestSupport.GetGoldenPath("eventlog.jsonl"), eventLogPath);

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
        var tempDir = GoldenArtifactTestSupport.CreateTempDir("aos-replaycli-tests");
        try
        {
            var manifestPath = Path.Combine(tempDir, "manifest.json");
            var eventLogPath = Path.Combine(tempDir, "eventlog.jsonl");
            File.Copy(GoldenArtifactTestSupport.GetGoldenPath("manifest.json"), manifestPath);

            var entry = new EventLogEntry(
                RunId: "run-mismatch",
                EventType: "workflow.hello",
                Data: new { message = "hello", manifestVersion = SchemaVersions.CurrentManifestVersion },
                OccurredAtUtc: new DateTimeOffset(2026, 2, 26, 19, 0, 0, TimeSpan.Zero));
            await File.WriteAllTextAsync(
                eventLogPath,
                GoldenArtifactTestSupport.SerializeSignedEventLogLines([entry], TestHmacKey, TestHmacKeyId) + "\n");

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
        var tempDir = GoldenArtifactTestSupport.CreateTempDir("aos-replaycli-tests");
        try
        {
            var manifestPath = Path.Combine(tempDir, "manifest.json");
            var eventLogPath = Path.Combine(tempDir, "eventlog.jsonl");
            File.Copy(GoldenArtifactTestSupport.GetGoldenPath("manifest.json"), manifestPath);

            var entry = new EventLogEntry(
                RunId: "run-golden-hello-1",
                EventType: "workflow.hello",
                Data: new { message = "hello", manifestVersion = "0.9" },
                OccurredAtUtc: new DateTimeOffset(2026, 2, 26, 19, 0, 0, TimeSpan.Zero));
            await File.WriteAllTextAsync(
                eventLogPath,
                GoldenArtifactTestSupport.SerializeSignedEventLogLines([entry], TestHmacKey, TestHmacKeyId) + "\n");

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
        var tempDir = GoldenArtifactTestSupport.CreateTempDir("aos-replaycli-tests");
        try
        {
            var manifestPath = Path.Combine(tempDir, "manifest.json");
            var eventLogPath = Path.Combine(tempDir, "eventlog.jsonl");

            await File.WriteAllTextAsync(manifestPath, """
                {"manifest":{"manifestVersion":"","runId":"run-1","seed":{"seedId":"","algorithm":"","value":0,"derivation":null},"timeSource":{"mode":"invalid","source":"","clockId":"","precision":"","notes":null},"models":[],"tools":[],"policyDecisions":[],"eventLog":{"schemaVersion":"","recordCount":0,"lastChainMac":""},"startedAtUtc":"2026-02-26T19:00:00+00:00","completedAtUtc":null},"integrity":{"algorithm":"HMAC-SHA256","keyId":"golden-key-1","manifestMac":"bad"}}
                """);
            await File.WriteAllTextAsync(eventLogPath, File.ReadAllText(GoldenArtifactTestSupport.GetGoldenPath("eventlog.jsonl")));

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
        var manifestPath = GoldenArtifactTestSupport.GetGoldenPath("manifest.json");
        var eventLogPath = GoldenArtifactTestSupport.GetGoldenPath("eventlog.jsonl");
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
        var manifestPath = GoldenArtifactTestSupport.GetGoldenPath("manifest.json");
        var eventLogPath = GoldenArtifactTestSupport.GetGoldenPath("eventlog.jsonl");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await ReplayCliRunner.RunAsync(
            ["--workflow", "unknown", "--manifest", manifestPath, "--eventlog", eventLogPath, "--hmac-key", TestHmacKey],
            stdout,
            stderr,
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("Unknown workflow 'unknown'. Available workflows: hello, planner.", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_WhenEventLogChainMacIsInvalid_ReturnsIntegrityFailure()
    {
        var tempDir = GoldenArtifactTestSupport.CreateTempDir("aos-replaycli-tests");
        try
        {
            var manifestPath = Path.Combine(tempDir, "manifest.json");
            var eventLogPath = Path.Combine(tempDir, "eventlog.jsonl");
            File.Copy(GoldenArtifactTestSupport.GetGoldenPath("manifest.json"), manifestPath);

            var records = GoldenArtifactTestSupport.ReadGoldenEventLogRecords().ToArray();
            var tamperedRecord = records[0] with
            {
                Entry = records[0].Entry with
                {
                    Data = new
                    {
                        invocationId = "run-golden-hello-1:hello-tool:0",
                        toolId = "noop",
                        toolVersion = "0.0",
                        status = "succeeded",
                        inputJson = "{\"message\":\"hello\",\"runId\":\"run-golden-hello-1\"}",
                        outputJson = "{\"message\":\"tampered\",\"runId\":\"run-golden-hello-1\"}",
                        error = (string?)null
                    }
                }
            };
            records[0] = tamperedRecord;
            await File.WriteAllTextAsync(
                eventLogPath,
                GoldenArtifactTestSupport.SerializeEventLogLines(records));

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
        var tempDir = GoldenArtifactTestSupport.CreateTempDir("aos-replaycli-tests");
        try
        {
            var manifestPath = Path.Combine(tempDir, "manifest.json");
            var eventLogPath = Path.Combine(tempDir, "eventlog.jsonl");
            var manifestRecord = GoldenArtifactTestSupport.ReadGoldenManifestRecord();
            File.Copy(GoldenArtifactTestSupport.GetGoldenPath("eventlog.jsonl"), eventLogPath);

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
                    GoldenArtifactTestSupport.JsonOptions));

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
        var tempDir = GoldenArtifactTestSupport.CreateTempDir("aos-replaycli-tests");
        try
        {
            var manifestPath = Path.Combine(tempDir, "manifest.json");
            var eventLogPath = Path.Combine(tempDir, "eventlog.jsonl");
            var manifest = GoldenArtifactTestSupport.ReadGoldenManifestRecord().Manifest with
            {
                EventLog = new EventLogSummary(
                    SchemaVersions.CurrentEventLogSchemaVersion,
                    99,
                    "mismatch-chain-mac")
            };

            await File.WriteAllTextAsync(
                manifestPath,
                GoldenArtifactTestSupport.SerializeSignedManifest(manifest, TestHmacKey, TestHmacKeyId));
            File.Copy(GoldenArtifactTestSupport.GetGoldenPath("eventlog.jsonl"), eventLogPath);

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = await ReplayCliRunner.RunAsync(
                ["--workflow", "hello", "--manifest", manifestPath, "--eventlog", eventLogPath, "--hmac-key", TestHmacKey],
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Contains(
                "Artifact compatibility failed: Manifest eventLog recordCount '99' does not match event log line count '2'.",
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
}
