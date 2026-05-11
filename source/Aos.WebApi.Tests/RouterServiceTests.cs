using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Aos.WebApi.Services;
using Xunit;

namespace Aos.WebApi.Tests;

public sealed class RouterServiceTests
{
    [Fact]
    public void SelectModel_ReturnsHighestScoringEligibleCandidate()
    {
        var service = CreateService(CreateDefaultOptions());

        var result = service.SelectModel(new RouterSelectionRequest(
            TaskClass: "chat.response",
            MaxLatencyMs: 220,
            MaxCostPer1KTokens: 0.5m,
            MinQualityScore: 60,
            RequiredComplianceTags: ["eu", "standard"]));

        Assert.NotNull(result.SelectedCandidate);
        Assert.Equal("openai-gpt-4.1-mini", result.SelectedCandidate!.ModelId);
        Assert.Null(result.Policy.PolicyId);
        Assert.Equal(["eu", "standard"], result.Policy.EffectiveConstraints.RequiredComplianceTags);
        Assert.Single(result.RankedCandidates);
        Assert.NotEmpty(result.RejectionReasons);
    }

    [Fact]
    public void SelectModel_FiltersCandidatesUsingDeterministicConstraints()
    {
        var service = CreateService(CreateDefaultOptions());

        var result = service.SelectModel(new RouterSelectionRequest(
            TaskClass: "audited.agent",
            MaxLatencyMs: 300,
            MaxCostPer1KTokens: 1.0m,
            MinQualityScore: 80,
            RequiredComplianceTags: ["audit", "eu"]));

        Assert.NotNull(result.SelectedCandidate);
        Assert.Equal("azure-gpt-4.1", result.SelectedCandidate!.ModelId);
        Assert.Contains(result.RejectionReasons, reason => reason.Contains("local/local-phi-mini/0.3", StringComparison.Ordinal));
        Assert.Contains(result.RejectionReasons, reason => reason.Contains("openai/openai-gpt-4.1-mini/2026-02", StringComparison.Ordinal));
    }

    [Fact]
    public void SelectModel_ReturnsStableDecisionWhenCandidateOrderChanges()
    {
        var request = new RouterSelectionRequest(
            TaskClass: "chat.response",
            MaxLatencyMs: 400,
            MaxCostPer1KTokens: 1.0m,
            MinQualityScore: 50,
            RequiredComplianceTags: ["eu"]);

        var forward = CreateService(CreateDefaultOptions()).SelectModel(request);
        var reversedOptions = CreateDefaultOptions();
        reversedOptions.Candidates.Reverse();
        var reversed = CreateService(reversedOptions).SelectModel(request);

        Assert.NotNull(forward.SelectedCandidate);
        Assert.NotNull(reversed.SelectedCandidate);
        Assert.Equal(forward.SelectedCandidate!.ModelId, reversed.SelectedCandidate!.ModelId);
        Assert.Equal(forward.SelectedCandidate.Provider, reversed.SelectedCandidate.Provider);
        Assert.Equal(forward.SelectedCandidate.Version, reversed.SelectedCandidate.Version);
        Assert.Equal(
            forward.RankedCandidates.Select(candidate => candidate.Candidate.ModelId),
            reversed.RankedCandidates.Select(candidate => candidate.Candidate.ModelId));
    }

    [Fact]
    public void SelectModel_UsesLexicalTieBreakWhenScoresAreEqual()
    {
        var options = new RouterOptions
        {
            Weights = new RouterWeightsOptions
            {
                Latency = 1m,
                Cost = 1m,
                Quality = 1m,
                Compliance = 1m
            },
            Candidates =
            [
                new RouterModelOptions
                {
                    ModelId = "model-z",
                    Provider = "openai",
                    Version = "1",
                    LatencyMs = 100,
                    CostPer1KTokens = 0.1m,
                    QualityScore = 80,
                    ComplianceScore = 90,
                    ComplianceTags = [ "eu" ]
                },
                new RouterModelOptions
                {
                    ModelId = "model-a",
                    Provider = "openai",
                    Version = "1",
                    LatencyMs = 100,
                    CostPer1KTokens = 0.1m,
                    QualityScore = 80,
                    ComplianceScore = 90,
                    ComplianceTags = [ "eu" ]
                }
            ]
        };

        var result = CreateService(options).SelectModel(new RouterSelectionRequest(
            TaskClass: "tie.break",
            MaxLatencyMs: 200,
            MaxCostPer1KTokens: 0.2m,
            MinQualityScore: 70,
            RequiredComplianceTags: ["eu"]));

        Assert.NotNull(result.SelectedCandidate);
        Assert.Equal("model-a", result.SelectedCandidate!.ModelId);
    }

    [Fact]
    public void SelectModel_WhenNothingMatches_ReturnsRejectionsAndNoSelection()
    {
        var service = CreateService(CreateDefaultOptions());

        var result = service.SelectModel(new RouterSelectionRequest(
            TaskClass: "strict.offline",
            MaxLatencyMs: 20,
            MaxCostPer1KTokens: 0.01m,
            MinQualityScore: 95,
            RequiredComplianceTags: ["offline", "audit"]));

        Assert.Null(result.SelectedCandidate);
        Assert.Empty(result.RankedCandidates);
        Assert.NotEmpty(result.RejectionReasons);
    }

    [Fact]
    public void SelectModel_AppliesTaskClassPolicyDefaults()
    {
        var options = CreateDefaultOptions();
        options.Policies =
        [
            new RouterPolicyOptions
            {
                PolicyId = "audit-quality",
                TaskClass = "audited.agent",
                MaxLatencyMs = 300,
                MaxCostPer1KTokens = 1.0m,
                MinQualityScore = 80,
                RequiredComplianceTags = [ "audit", "eu" ],
                Weights = new RouterWeightsOptions
                {
                    Latency = 0.15m,
                    Cost = 0.1m,
                    Quality = 0.45m,
                    Compliance = 0.3m
                }
            }
        ];

        var result = CreateService(options).SelectModel(new RouterSelectionRequest(
            TaskClass: "audited.agent",
            MaxLatencyMs: null,
            MaxCostPer1KTokens: null,
            MinQualityScore: null,
            RequiredComplianceTags: null));

        Assert.NotNull(result.SelectedCandidate);
        Assert.Equal("azure-gpt-4.1", result.SelectedCandidate!.ModelId);
        Assert.Equal("audit-quality", result.Policy.PolicyId);
        Assert.Equal(300, result.Policy.EffectiveConstraints.MaxLatencyMs);
        Assert.Equal(1.0m, result.Policy.EffectiveConstraints.MaxCostPer1KTokens);
        Assert.Equal(80, result.Policy.EffectiveConstraints.MinQualityScore);
        Assert.Equal(["audit", "eu"], result.Policy.EffectiveConstraints.RequiredComplianceTags);
        Assert.Equal(0.45m, result.Policy.EffectiveWeights.Quality);
    }

    [Fact]
    public void SelectModel_MergesRequestWithPolicyUsingStricterConstraints()
    {
        var options = CreateDefaultOptions();
        options.Policies =
        [
            new RouterPolicyOptions
            {
                PolicyId = "hello-balanced-eu",
                TaskClass = "workflow.hello",
                MaxLatencyMs = 300,
                MaxCostPer1KTokens = 1.0m,
                MinQualityScore = 60,
                RequiredComplianceTags = [ "eu" ]
            }
        ];

        var result = CreateService(options).SelectModel(new RouterSelectionRequest(
            TaskClass: "workflow.hello",
            MaxLatencyMs: 220,
            MaxCostPer1KTokens: 0.5m,
            MinQualityScore: 70,
            RequiredComplianceTags: [ "standard" ]));

        Assert.NotNull(result.SelectedCandidate);
        Assert.Equal("openai-gpt-4.1-mini", result.SelectedCandidate!.ModelId);
        Assert.Equal(220, result.Policy.EffectiveConstraints.MaxLatencyMs);
        Assert.Equal(0.5m, result.Policy.EffectiveConstraints.MaxCostPer1KTokens);
        Assert.Equal(70, result.Policy.EffectiveConstraints.MinQualityScore);
        Assert.Equal(["eu", "standard"], result.Policy.EffectiveConstraints.RequiredComplianceTags);
    }

    [Fact]
    public async Task SelectModel_RemainsStableUnderConcurrentAccess()
    {
        var service = CreateService(CreateDefaultOptions());
        var request = new RouterSelectionRequest(
            TaskClass: "chat.response",
            MaxLatencyMs: 400,
            MaxCostPer1KTokens: 1.0m,
            MinQualityScore: 50,
            RequiredComplianceTags: ["eu"]);

        var expected = service.SelectModel(request);
        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => service.SelectModel(request))));

        Assert.All(results, result =>
        {
            Assert.Equal(expected.SelectedCandidate, result.SelectedCandidate);
            Assert.Equal(
                expected.RankedCandidates.Select(candidate => candidate.Candidate.ModelId),
                result.RankedCandidates.Select(candidate => candidate.Candidate.ModelId));
            Assert.Equal(expected.RejectionReasons, result.RejectionReasons);
        });
    }

    private static DeterministicRouterService CreateService(RouterOptions options)
        => new(Microsoft.Extensions.Options.Options.Create(options));

    private static RouterOptions CreateDefaultOptions()
    {
        return new RouterOptions
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
                },
                new RouterModelOptions
                {
                    ModelId = "azure-gpt-4.1",
                    Provider = "azure-openai",
                    Version = "2026-02",
                    LatencyMs = 260,
                    CostPer1KTokens = 0.9m,
                    QualityScore = 91,
                    ComplianceScore = 88,
                    ComplianceTags = [ "standard", "eu", "audit" ]
                }
            ]
        };
    }
}
