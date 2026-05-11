using Aos.ReplayCli;
using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Aos.WebApi.Services;
using Xunit;

namespace Aos.WebApi.Tests;

public sealed class RouterBenchmarkTests
{
    [Fact]
    public void Run_ReportsLatencySummaryWithoutChangingRouterDecision()
    {
        var router = CreateRouter(CreateOptions());
        var request = CreateRequest();
        var before = router.SelectModel(request);

        var report = RouterBenchmarkRunner.Run(router, request, new RouterBenchmarkOptions(
            Iterations: 25,
            WarmupIterations: 5));
        var after = router.SelectModel(request);

        Assert.Equal("workflow.hello", report.TaskClass);
        Assert.Equal(25, report.Iterations);
        Assert.Equal(5, report.WarmupIterations);
        Assert.Equal("openai-gpt-4.1-mini", report.SelectedModelId);
        Assert.True(report.MinLatencyMs >= 0);
        Assert.True(report.MedianLatencyMs >= report.MinLatencyMs);
        Assert.True(report.P95LatencyMs >= report.MedianLatencyMs);
        Assert.True(report.MaxLatencyMs >= report.P95LatencyMs);
        Assert.Equal(before.SelectedCandidate, after.SelectedCandidate);
        Assert.Equal(
            before.RankedCandidates.Select(candidate => candidate.Candidate.ModelId),
            after.RankedCandidates.Select(candidate => candidate.Candidate.ModelId));
    }

    [Fact]
    public async Task BenchmarkRouterCli_ReturnsReportFromConfig()
    {
        var tempDir = GoldenArtifactTestSupport.CreateTempDir("aos-router-benchmark-tests");
        try
        {
            var configPath = Path.Combine(tempDir, "appsettings.json");
            await File.WriteAllTextAsync(configPath, """
                {
                  "Router": {
                    "Weights": {
                      "Latency": 0.35,
                      "Cost": 0.2,
                      "Quality": 0.3,
                      "Compliance": 0.15
                    },
                    "Candidates": [
                      {
                        "ModelId": "openai-gpt-4.1-mini",
                        "Provider": "openai",
                        "Version": "2026-02",
                        "LatencyMs": 180,
                        "CostPer1KTokens": 0.4,
                        "QualityScore": 82,
                        "ComplianceScore": 90,
                        "ComplianceTags": [ "standard", "eu" ]
                      }
                    ],
                    "Policies": [
                      {
                        "PolicyId": "hello-balanced-eu",
                        "TaskClass": "workflow.hello",
                        "MaxLatencyMs": 220,
                        "MaxCostPer1KTokens": 0.5,
                        "MinQualityScore": 60,
                        "RequiredComplianceTags": [ "standard", "eu" ]
                      }
                    ]
                  }
                }
                """);

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = await RouterBenchmarkCliRunner.RunAsync(
                [
                    "--config", configPath,
                    "--iterations", "5",
                    "--warmup", "1",
                    "--task-class", "workflow.hello"
                ],
                stdout,
                stderr,
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Contains("Router benchmark: workflow.hello", stdout.ToString());
            Assert.Contains("iterations: 5", stdout.ToString());
            Assert.Contains("selected: openai/openai-gpt-4.1-mini/2026-02", stdout.ToString());
            Assert.Contains("latency.p95Ms:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static RouterSelectionRequest CreateRequest() => new(
        TaskClass: "workflow.hello",
        MaxLatencyMs: 220,
        MaxCostPer1KTokens: 0.5m,
        MinQualityScore: 60,
        RequiredComplianceTags: ["eu", "standard"]);

    private static DeterministicRouterService CreateRouter(RouterOptions options) =>
        new(Microsoft.Extensions.Options.Options.Create(options));

    private static RouterOptions CreateOptions() => new()
    {
        Weights = new RouterWeightsOptions
        {
            Latency = 0.35m,
            Cost = 0.2m,
            Quality = 0.3m,
            Compliance = 0.15m
        },
        Candidates =
        [
            new RouterModelOptions
            {
                ModelId = "openai-gpt-4.1-mini",
                Provider = "openai",
                Version = "2026-02",
                LatencyMs = 180,
                CostPer1KTokens = 0.4m,
                QualityScore = 82,
                ComplianceScore = 90,
                ComplianceTags = [ "standard", "eu" ]
            },
            new RouterModelOptions
            {
                ModelId = "local-phi-mini",
                Provider = "local",
                Version = "0.3",
                LatencyMs = 45,
                CostPer1KTokens = 0.05m,
                QualityScore = 58,
                ComplianceScore = 95,
                ComplianceTags = [ "standard", "offline", "eu" ]
            }
        ]
    };
}
