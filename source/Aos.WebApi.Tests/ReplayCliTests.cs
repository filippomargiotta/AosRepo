using System.Text.Json;
using Aos.ReplayCli;
using Aos.WebApi.Models;
using Xunit;

namespace Aos.WebApi.Tests;

public sealed class ReplayCliTests
{
    [Fact]
    public async Task RunAsync_WithGoldenArtifacts_ReturnsSuccess()
    {
        var manifestPath = GetGoldenPath("manifest.json");
        var eventLogPath = GetGoldenPath("eventlog.jsonl");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await ReplayCliRunner.RunAsync(
            ["--workflow", "hello", "--manifest", manifestPath, "--eventlog", eventLogPath],
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
            var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            await File.WriteAllTextAsync(eventLogPath, json + "\n");

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = await ReplayCliRunner.RunAsync(
                ["--workflow", "hello", "--manifest", manifestPath, "--eventlog", eventLogPath],
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Contains("Mismatch: Event log bytes differ from replay output.", stderr.ToString());
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
            ["--workflow", "hello", "--manifest", "/no/such/manifest.json", "--eventlog", "/no/such/eventlog.jsonl"],
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
                ["--workflow", "hello", "--manifest", manifestPath, "--eventlog", eventLogPath],
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
            ["--manifest", manifestPath, "--eventlog", eventLogPath],
            stdout,
            stderr,
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("The --workflow, --manifest, and --eventlog arguments are required.", stderr.ToString());
        Assert.Contains("Usage: aos-replay --workflow <name> --manifest <path> --eventlog <path>", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_WhenWorkflowIsUnknown_ReturnsUsageErrorCode()
    {
        var manifestPath = GetGoldenPath("manifest.json");
        var eventLogPath = GetGoldenPath("eventlog.jsonl");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await ReplayCliRunner.RunAsync(
            ["--workflow", "unknown", "--manifest", manifestPath, "--eventlog", eventLogPath],
            stdout,
            stderr,
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("Unknown workflow 'unknown'. Available workflows: hello.", stderr.ToString());
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
}
