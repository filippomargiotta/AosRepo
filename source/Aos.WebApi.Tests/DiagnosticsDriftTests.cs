using Aos.ReplayCli;
using Aos.WebApi.Models;
using Xunit;

namespace Aos.WebApi.Tests;

public sealed class DiagnosticsDriftTests
{
    [Fact]
    public void Cusum_StaysQuietWithinSlackAndAlarmsOnSustainedShift()
    {
        var quiet = DiagnosticsDrift.Cusum("q", "ms", [10, 10.4, 9.8, 10.3, 10, 10.2], 10, 0.5, 2, gating: false);
        Assert.Null(quiet.AlarmIteration);
        var shifted = DiagnosticsDrift.Cusum("s", "ms", [10, 10, 12, 12, 12, 12], 10, 0.5, 2, gating: false);
        Assert.Equal(4, shifted.AlarmIteration); // 1.5 stays under the threshold; 3.0 at the second shifted point exceeds 2
        Assert.Equal([0, 0, 1.5, 3, 4.5, 6], shifted.Statistics);
    }

    [Fact]
    public void Cusum_IsDeterministicAndIgnoresDownwardMoves()
    {
        double[] values = [5, 20, 5, 5, 5];
        var first = DiagnosticsDrift.Cusum("x", "ms", values, 10, 1, 8, gating: false);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(first),
            System.Text.Json.JsonSerializer.Serialize(DiagnosticsDrift.Cusum("x", "ms", values, 10, 1, 8, gating: false)));
        Assert.All(first.Statistics, s => Assert.True(s >= 0));
        Assert.Equal([0.0, 9.0, 3.0, 0.0, 0.0], first.Statistics);
    }

    [Fact]
    public void Percentile_UsesNearestRankIndependentOfOrder()
    {
        Assert.Equal(3, DiagnosticsDrift.Percentile([5, 1, 3, 2, 4], 0.5));
        Assert.Equal(5, DiagnosticsDrift.Percentile([4, 2, 5, 1, 3], 0.95));
        Assert.Equal(0, DiagnosticsDrift.Percentile([], 0.5));
    }

    [Fact]
    public void StageFailureDrift_AlarmsOnASingleFailedIteration()
    {
        DiagnosticsIterationResult Iteration(int i, int failures) => new(i, 12, 12, failures, 0.3, 100);
        Assert.Null(DiagnosticsDrift.StageFailureDrift([Iteration(1, 0), Iteration(2, 0)]).AlarmIteration);
        Assert.Equal(2, DiagnosticsDrift.StageFailureDrift([Iteration(1, 0), Iteration(2, 1)]).AlarmIteration);
    }

    [Fact]
    public void LatencyDrift_IsRelativeToBaselineAndNeverGates()
    {
        var series = DiagnosticsDrift.LatencyDrift("tool", [100, 100, 100, 160, 170, 180], 3, 0.1, 0.5);
        Assert.Equal(100, series.Target);
        Assert.False(series.Gating);
        Assert.Equal(5, series.AlarmIteration);
    }

    [Fact]
    public void RepeatReport_FlagsInjectedRegressionAndUnstableOutcome()
    {
        var scenario = RepositoryDiagnosticsTests.Cases().Single(c => c.CaseId == "RDI-01");
        var artifacts = RepositoryDiagnosticsTests.Run(scenario);
        var good = DiagnosticsEvaluationRunner.Measure(scenario, artifacts, true, true);
        var goodRow = (good, DiagnosticsRepeatRunner.Signature(good, artifacts.Outcome));
        var bad = good with { TaskSuccess = false, Stages = new DiagnosticsStageResults(true, true, false, true, true) };
        var badRow = (bad, DiagnosticsRepeatRunner.Signature(bad, artifacts.Outcome));

        var clean = DiagnosticsRepeatRunner.Build("process-v1", 2, 1, [scenario],
            new Dictionary<(int, string), (DiagnosticsCaseMetrics, string)> { [(1, "RDI-01")] = goodRow, [(2, "RDI-01")] = goodRow }, 1, 0.5, 2);
        Assert.True(clean.Passed, string.Join("; ", clean.GateChecks.Where(c => !c.Passed).Select(c => c.Detail)));
        Assert.Equal(1, clean.Cases.Single().DistinctSignatures);

        var drifted = DiagnosticsRepeatRunner.Build("process-v1", 2, 1, [scenario],
            new Dictionary<(int, string), (DiagnosticsCaseMetrics, string)> { [(1, "RDI-01")] = goodRow, [(2, "RDI-01")] = badRow }, 1, 0.5, 2);
        Assert.False(drifted.Passed);
        Assert.All(new[] { "run-stability", "task-success", "stage-drift" }, name => Assert.False(drifted.GateChecks.Single(c => c.Name == name).Passed));
        Assert.Equal(1, drifted.Cases.Single().StageFailures["evidence"]);
        Assert.Equal(2, drifted.Drift.Single(d => d.Name == "stage-failures").AlarmIteration);
    }

    [Fact]
    public void Signature_IgnoresRunIdsAndTimingButNotTheDiagnosis()
    {
        var scenario = RepositoryDiagnosticsTests.Cases().Single(c => c.CaseId == "RDI-01");
        var first = RepositoryDiagnosticsTests.Run(scenario);
        var second = RepositoryDiagnosticsTests.Run(scenario);
        var m1 = DiagnosticsEvaluationRunner.Measure(scenario, first, true, true);
        var m2 = DiagnosticsEvaluationRunner.Measure(scenario, second, true, true) with { ToolDurationMs = 9999, PlannerOverheadMs = 9999 };
        Assert.Equal(DiagnosticsRepeatRunner.Signature(m1, first.Outcome), DiagnosticsRepeatRunner.Signature(m2, second.Outcome));
        Assert.NotEqual(DiagnosticsRepeatRunner.Signature(m1, first.Outcome),
            DiagnosticsRepeatRunner.Signature(m1, first.Outcome with { Diagnosis = "something else" }));
    }

    [ContainerFact]
    public async Task ContainerBackend_RepeatedConcurrentRunsAreStable()
    {
        var dir = GoldenArtifactTestSupport.CreateTempDir("aos-november-repeat");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var result = await DiagnosticsRepeatRunner.RunAsync(["--snapshot-dir", RepositoryDiagnosticsTests.SnapshotRoot, "--snapshot-dir-v2", RepositoryDiagnosticsTests.SnapshotRootV2,
            "--cases-root", RepositoryDiagnosticsTests.CasesRoot, "--output-dir", dir, "--hmac-key", GoldenArtifactTestSupport.GoldenHmacKey,
            "--executor", "container-v1", "--runs", "3", "--concurrency", "4"], stdout, stderr, CancellationToken.None);
        Assert.True(result == 0, stdout + "\n" + stderr);
        Assert.True(File.Exists(Path.Combine(dir, "repeat-report.json")));
    }

    [Fact]
    public async Task RepeatRunner_RejectsUnknownAndOutOfRangeOptions()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Assert.Equal(2, await DiagnosticsRepeatRunner.RunAsync(["--bogus", "1"], stdout, stderr, CancellationToken.None));
        Assert.Equal(2, await DiagnosticsRepeatRunner.RunAsync(["--snapshot-dir", "a", "--cases-root", "b", "--output-dir", "c", "--hmac-key", "k", "--runs", "0"], stdout, stderr, CancellationToken.None));
    }
}
