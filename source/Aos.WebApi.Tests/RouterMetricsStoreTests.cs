using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Aos.WebApi.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aos.WebApi.Tests;

public sealed class RouterMetricsStoreTests
{
    [Fact]
    public void TryGetMetric_ReturnsMetricForExactCandidateIdentity()
    {
        var candidate = RouterTestData.CreateCandidate(
            modelId: "openai-gpt-4.1-mini",
            provider: "openai",
            version: "2026-02");
        var store = new InMemoryRouterMetricsStore(
        [
            CreateMetric(
                taskClass: "workflow.hello",
                provider: "openai",
                modelId: "openai-gpt-4.1-mini",
                version: "2026-02")
        ]);

        var found = store.TryGetMetric("workflow.hello", candidate, out var metric);

        Assert.True(found);
        Assert.NotNull(metric);
        Assert.Equal("workflow.hello", metric!.TaskClass);
        Assert.Equal("openai-gpt-4.1-mini", metric.ModelId);
        Assert.Equal(176, metric.ObservedLatencyMs);
    }

    [Fact]
    public void TryGetMetric_WhenCandidateIdentityDiffers_ReturnsFalse()
    {
        var candidate = RouterTestData.CreateCandidate(
            modelId: "openai-gpt-4.1-mini",
            provider: "OpenAI",
            version: "2026-02");
        var store = new InMemoryRouterMetricsStore(
        [
            CreateMetric(
                taskClass: "workflow.hello",
                provider: "openai",
                modelId: "openai-gpt-4.1-mini",
                version: "2026-02")
        ]);

        var found = store.TryGetMetric("workflow.hello", candidate, out var metric);

        Assert.False(found);
        Assert.Null(metric);
    }

    [Fact]
    public void TryGetMetric_WhenMetricIsMissing_ReturnsFalse()
    {
        var candidate = RouterTestData.CreateCandidate(
            modelId: "missing-model",
            provider: "openai",
            version: "2026-02");
        var store = new InMemoryRouterMetricsStore(
        [
            CreateMetric(
                taskClass: "workflow.hello",
                provider: "openai",
                modelId: "openai-gpt-4.1-mini",
                version: "2026-02")
        ]);

        var found = store.TryGetMetric("workflow.hello", candidate, out var metric);

        Assert.False(found);
        Assert.Null(metric);
    }

    [Fact]
    public void ListMetrics_ReturnsStableOrderIndependentOfInputOrder()
    {
        var store = new InMemoryRouterMetricsStore(
        [
            CreateMetric(provider: "openai", modelId: "model-z", version: "2"),
            CreateMetric(provider: "azure-openai", modelId: "model-b", version: "1"),
            CreateMetric(provider: "openai", modelId: "model-a", version: "1")
        ]);

        var metrics = store.ListMetrics("workflow.hello");

        Assert.Equal(
            [
                "azure-openai/model-b/1",
                "openai/model-a/1",
                "openai/model-z/2"
            ],
            metrics.Select(metric => $"{metric.Provider}/{metric.ModelId}/{metric.Version}"));
    }

    [Fact]
    public void Constructor_WhenDuplicateMetricKeysExist_Throws()
    {
        var duplicateMetric = CreateMetric();

        var ex = Assert.Throws<InvalidOperationException>(() => new InMemoryRouterMetricsStore(
        [
            duplicateMetric,
            duplicateMetric
        ]));

        Assert.Contains("duplicate metric entry", ex.Message);
    }

    [Fact]
    public void Constructor_LoadsMetricsFromOptions()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new RouterMetricsOptions
        {
            Metrics =
            [
                new RouterModelMetricOptions
                {
                    TaskClass = "workflow.hello",
                    Provider = "openai",
                    ModelId = "openai-gpt-4.1-mini",
                    Version = "2026-02",
                    ObservedLatencyMs = 176,
                    SuccessRate = 0.995m,
                    QualityScore = 83,
                    SampleCount = 240,
                    CapturedAtUtc = DateTimeOffset.Parse("2026-05-19T00:00:00Z"),
                    Source = "test-fixture"
                }
            ]
        });
        var candidate = RouterTestData.CreateCandidate(
            modelId: "openai-gpt-4.1-mini",
            provider: "openai",
            version: "2026-02");

        var store = new InMemoryRouterMetricsStore(options);
        var found = store.TryGetMetric("workflow.hello", candidate, out var metric);

        Assert.True(found);
        Assert.NotNull(metric);
        Assert.Equal("test-fixture", metric!.Source);
    }

    private static RouterModelPerformanceMetric CreateMetric(
        string taskClass = "workflow.hello",
        string provider = "openai",
        string modelId = "openai-gpt-4.1-mini",
        string version = "2026-02") => new(
            TaskClass: taskClass,
            Provider: provider,
            ModelId: modelId,
            Version: version,
            ObservedLatencyMs: 176,
            SuccessRate: 0.995m,
            QualityScore: 83,
            SampleCount: 240,
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-19T00:00:00Z"),
            Source: "test-fixture");
}
