using Aos.ReplayCli;
using Aos.WebApi.Models;
using Aos.WebApi.Services;
using Xunit;

namespace Aos.WebApi.Tests;

public sealed class DiagnosticsScoringTests
{
    private static DiagnosticsEvaluationCase Case(string id) => RepositoryDiagnosticsTests.Cases().Single(c => c.CaseId == id);

    private static (DiagnosticsEvaluationCase Scenario, DiagnosticsOutcome Outcome, IReadOnlyList<ToolExecutionEvent> Tools) Recorded(string id)
    {
        var scenario = Case(id);
        var artifacts = RepositoryDiagnosticsTests.Run(scenario);
        return (scenario, artifacts.Outcome, RepositoryDiagnosticsTests.Events<ToolExecutionEvent>(artifacts, "tool.execution"));
    }

    [Fact]
    public void FixtureV2_IsPinnedWhileV1StaysFrozen()
    {
        var v1 = RepositoryDiagnosticsDefaults.ImportFixture(RepositoryDiagnosticsTests.SnapshotRoot).Reference;
        Assert.Equal("6c13d7a97ce3587b55e39073f7394316954ea94a79f8dad5e063e0de8670c9f9", v1.ContentHash);
        Assert.Equal(RepositoryDiagnosticsDefaults.Revision, v1.SourceRevision);
        var v2 = RepositoryDiagnosticsDefaults.ImportFixture(RepositoryDiagnosticsTests.SnapshotRootV2).Reference;
        Assert.Equal(RepositoryDiagnosticsDefaults.FixtureV2ContentHash, v2.ContentHash);
        Assert.Equal(RepositoryDiagnosticsDefaults.RevisionV2, v2.SourceRevision);
        Assert.Equal(Case("RDI-03").Request.Snapshot, v2);
        // Every v1 file survives unchanged inside v2.
        foreach (var file in Directory.EnumerateFiles(RepositoryDiagnosticsTests.SnapshotRoot, "*", SearchOption.AllDirectories)
                     .Where(f => !f.EndsWith("README.md", StringComparison.Ordinal)))
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(RepositoryDiagnosticsTests.SnapshotRootV2,
                Path.GetRelativePath(RepositoryDiagnosticsTests.SnapshotRoot, file))));
    }

    [Theory]
    [InlineData("RDI-03")]
    [InlineData("RDI-04")]
    [InlineData("RDI-05")]
    public void NewCases_ScoreFullyOnRecordedEvidenceWithoutUnrelatedReads(string id)
    {
        var (scenario, outcome, tools) = Recorded(id);
        var score = DiagnosticsCaseScorer.Evaluate(scenario, outcome, tools, true);
        Assert.True(score.Passed, string.Join(",", score.Failures));
        Assert.Equal(1, score.ExactScore);
        Assert.Equal(1, score.SemanticScore);
        Assert.All(tools.Where(t => t.ToolId == "repo.read"), t =>
            Assert.Contains(System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(t.InputJson)!["path"], scenario.Scoring!.AllowedReadPaths!));
    }

    [Fact]
    public void Scorer_AcceptsHarmlessFormattingAndWordingChanges()
    {
        var (scenario, outcome, tools) = Recorded("RDI-01");
        foreach (var diagnosis in new[]
        {
            "  APPLYDISCOUNT returns the Discount Amount (20.0)\ninstead of the remaining price, 80.00 -- the multiplication omits the complement.  ",
            "applydiscount: discount value 20 comes back where the price after discount (80) is expected"
        })
        {
            var score = DiagnosticsCaseScorer.Evaluate(scenario, outcome with { Diagnosis = diagnosis }, tools, true);
            Assert.True(score.Passed, string.Join(",", score.Failures));
            Assert.Equal(1, score.SemanticScore);
            Assert.True(score.ExactScore < 1);
        }
    }

    [Theory]
    [InlineData("The failure is a rounding error in the tax calculation.")]
    [InlineData("ApplyDiscount returns the discount amount instead of the remaining price, caused by rounding of the result (80).")]
    [InlineData("ApplyDiscount returns the discount amount instead of the remaining price.")]
    public void Scorer_RejectsUnsupportedOrIncompleteDiagnoses(string diagnosis)
    {
        var (scenario, outcome, tools) = Recorded("RDI-01");
        var score = DiagnosticsCaseScorer.Evaluate(scenario, outcome with { Diagnosis = diagnosis }, tools, true);
        Assert.False(score.Passed);
        Assert.True(score.SemanticScore < 1);
    }

    [Fact]
    public void Scorer_RejectsEvidenceThatDoesNotSupportTheClaimOrLeavesScope()
    {
        var (scenario, outcome, tools) = Recorded("RDI-01");
        var wrongLine = outcome with { Evidence = outcome.Evidence.Select(e => e.Path == "Pricing/PriceCalculator.cs" ? e with { Line = 1 } : e).ToArray() };
        Assert.Contains("fact:Pricing/PriceCalculator.cs", DiagnosticsCaseScorer.Evaluate(scenario, wrongLine, tools, true).Failures);
        var missing = outcome with { Evidence = outcome.Evidence.Where(e => e.Path != "Pricing/PricingTests.cs").ToArray() };
        Assert.Contains("evidence.path:Pricing/PricingTests.cs", DiagnosticsCaseScorer.Evaluate(scenario, missing, tools, true).Failures);
        var stray = tools.Append(tools.First(t => t.ToolId == "repo.read") with
            { InvocationId = "stray", InputJson = "{\"path\":\"appsettings.json\"}" }).ToArray();
        Assert.Contains("scope:appsettings.json", DiagnosticsCaseScorer.Evaluate(scenario, outcome, stray, true).Failures);
    }

    [Fact]
    public void Scorer_RejectsAConfidentDiagnosisWhereNoneIsExpected()
    {
        var (scenario, outcome, tools) = Recorded("RDI-08");
        Assert.True(DiagnosticsCaseScorer.Evaluate(scenario, outcome, tools, true).Passed);
        var confident = outcome with { Status = "succeeded", ReasonCode = "evidence.grounded", Diagnosis = "Configuration issue." };
        Assert.False(DiagnosticsCaseScorer.Evaluate(scenario, confident, tools, true).Passed);
        Assert.False(DiagnosticsCaseScorer.Evaluate(scenario, outcome with { Diagnosis = "Configuration issue." }, tools, true).Passed);
    }

    [Fact]
    public void Stages_AttributeBaselineFailuresToTheFirstFailingStage()
    {
        DiagnosticsCaseMetrics Metrics(string id, int retries) =>
            DiagnosticsEvaluationRunner.Measure(Case(id), RepositoryDiagnosticsTests.Run(Case(id), retries), true, retries == 1);
        var timeout = Metrics("RDI-06", 0);
        Assert.False(timeout.TaskSuccess);
        Assert.Equal(new DiagnosticsStageResults(true, false, true, true, true), timeout.Stages);
        var noEvidence = Metrics("RDI-07", 0);
        Assert.Equal(new DiagnosticsStageResults(false, true, true, true, true), noEvidence.Stages);
        foreach (var id in new[] { "RDI-01", "RDI-03", "RDI-06", "RDI-07", "RDI-08", "RDI-09", "RDI-10", "RDI-11" })
        {
            var recovered = Metrics(id, 1);
            Assert.True(recovered.TaskSuccess, id);
            Assert.Equal(new DiagnosticsStageResults(true, true, true, true, true), recovered.Stages);
            Assert.True(recovered.AllowedActionsValid);
            Assert.Equal(1, recovered.ExactScore);
        }
    }

    [Fact]
    public void BaselineComparison_FlagsOnlyPreviouslyPassingCasesAndStages()
    {
        var good = DiagnosticsEvaluationRunner.Measure(Case("RDI-01"), RepositoryDiagnosticsTests.Run(Case("RDI-01")), true, true);
        var baseline = new DiagnosticsEvaluationReport("0.1", "test", Case("RDI-01").Request.Snapshot, [], [good], 0, 1, 0, false, true);
        Assert.Empty(DiagnosticsEvaluationRunner.CompareToBaseline(baseline, [good]));
        var worse = good with { TaskSuccess = false, Stages = good.Stages! with { Evidence = false } };
        Assert.Equal(["RDI-01: task success regressed", "RDI-01: evidence stage regressed"], DiagnosticsEvaluationRunner.CompareToBaseline(baseline, [worse]));
        Assert.Single(DiagnosticsEvaluationRunner.CompareToBaseline(baseline, []));
        // A baseline that already failed cannot regress further.
        var failedBefore = baseline with { PlannerV2 = [worse] };
        Assert.Empty(DiagnosticsEvaluationRunner.CompareToBaseline(failedBefore, [worse]));
    }
}
