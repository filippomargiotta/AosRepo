using Aos.ReplayCli;
using Xunit;

namespace Aos.WebApi.Tests;

public sealed class GoldenEvaluationRunnerTests
{
    private const string TestHmacKey = GoldenArtifactTestSupport.GoldenHmacKey;

    [Fact]
    public async Task RunAsync_WithGoldenScenario_ReturnsSuccessSummary()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await GoldenEvaluationRunner.RunAsync(
            ["--scenarios-root", GoldenArtifactTestSupport.GetGoldenRoot(), "--hmac-key", TestHmacKey],
            stdout,
            stderr,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("PASS hello-workflow-v1", stdout.ToString());
        Assert.Contains("Evaluated 1 scenario(s): 1 passed, 0 failed.", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_WhenScenarioReplayFails_ReturnsFailureSummary()
    {
        var tempDir = GoldenArtifactTestSupport.CreateTempDir("aos-eval");

        try
        {
            var scenarioDir = Path.Combine(tempDir, "hello-workflow-v1");
            Directory.CreateDirectory(scenarioDir);
            File.Copy(GoldenArtifactTestSupport.GetGoldenPath("scenario.json"), Path.Combine(scenarioDir, "scenario.json"));
            File.Copy(GoldenArtifactTestSupport.GetGoldenPath("manifest.json"), Path.Combine(scenarioDir, "manifest.json"));
            await File.WriteAllTextAsync(
                Path.Combine(scenarioDir, "eventlog.jsonl"),
                """
                {"schemaVersion":"0.2","entry":{"runId":"run-golden-hello-1","eventType":"workflow.hello","data":{"message":"tampered","manifestVersion":"0.3"},"occurredAtUtc":"2026-02-26T19:00:00+00:00"},"integrity":{"algorithm":"HMAC-SHA256","keyId":"golden-key-1","previousChainMac":null,"chainMac":"d63ea77315d4f7f0adf67a98b6c3cc8a404c8266258a5ddf03e0d758b19b59a5"}}
                """);

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = await GoldenEvaluationRunner.RunAsync(
                ["--scenarios-root", tempDir, "--hmac-key", TestHmacKey],
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Contains("FAIL hello-workflow-v1", stdout.ToString());
            Assert.Contains("Evaluated 1 scenario(s): 0 passed, 1 failed.", stdout.ToString());
            Assert.Contains("[hello-workflow-v1]", stderr.ToString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
