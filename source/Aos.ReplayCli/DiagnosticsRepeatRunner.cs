using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aos.WebApi.Models;
using Aos.WebApi.Services;

namespace Aos.ReplayCli;

public sealed record DiagnosticsRepeatCase(
    string CaseId, int Runs, int Successes, int DistinctSignatures, bool Stable, int ReplayFailures,
    IReadOnlyDictionary<string, int> StageFailures, string Signature);
public sealed record DiagnosticsRepeatLatency(LatencySummary Planner, LatencySummary Tool, LatencySummary Sandbox);
public sealed record DiagnosticsRepeatReport(
    string SchemaVersion, string Executor, int Iterations, int Concurrency, int TotalRuns,
    IReadOnlyList<DiagnosticsRepeatCase> Cases, IReadOnlyList<DiagnosticsIterationResult> IterationResults,
    DiagnosticsRepeatLatency Latency, IReadOnlyList<DriftSeries> Drift, IReadOnlyList<DiagnosticsGateCheck> GateChecks, bool Passed);

// Reruns the pinned Planner v2 dataset N times at a chosen concurrency and reports run-to-run stability, latency split by
// planner/tool/sandbox, and CUSUM drift. Every run is also replayed from its own signed artifacts.
public static class DiagnosticsRepeatRunner
{
    private static readonly string[] Known = ["--snapshot-dir", "--cases-root", "--output-dir", "--hmac-key", "--executor", "--worker-assembly",
        "--container-image", "--snapshot-dir-v2", "--runs", "--concurrency", "--baseline-runs", "--latency-slack", "--latency-threshold"];

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        try
        {
            var options = Parse(args);
            var runs = IntOption(options, "--runs", 5, 1, 1000);
            var concurrency = IntOption(options, "--concurrency", 1, 1, 64);
            var baselineRuns = IntOption(options, "--baseline-runs", 2, 1, 1000);
            var slack = DoubleOption(options, "--latency-slack", 0.5);
            var threshold = DoubleOption(options, "--latency-threshold", 2.0);
            var executor = options.GetValueOrDefault("--executor", "container-v1");
            if (executor is not ("container-v1" or "process-v1")) throw new ArgumentException("Executor must be container-v1 or process-v1.");
            var snapshot = RepositoryDiagnosticsDefaults.ImportFixture(options["--snapshot-dir"]);
            var snapshotDirs = new Dictionary<string, (string Dir, RepositorySnapshot Snapshot)>(StringComparer.Ordinal)
                { [snapshot.Reference.ContentHash] = (options["--snapshot-dir"], snapshot) };
            if (options.TryGetValue("--snapshot-dir-v2", out var v2Dir))
            {
                var second = RepositoryDiagnosticsDefaults.ImportFixture(v2Dir);
                if (!snapshotDirs.TryAdd(second.Reference.ContentHash, (v2Dir, second))) throw new ArgumentException("Snapshot directories must differ.");
            }
            var cases = DiagnosticsEvaluationRunner.LoadCases(options["--cases-root"], snapshotDirs);
            var outputRoot = Path.GetFullPath(options["--output-dir"]);
            Directory.CreateDirectory(outputRoot);
            var key = options["--hmac-key"];

            var results = new ConcurrentDictionary<(int Iteration, string CaseId), (DiagnosticsCaseMetrics Metrics, string Signature)>();
            using var gate = new SemaphoreSlim(concurrency);
            for (var iteration = 1; iteration <= runs; iteration++)
            {
                var current = iteration;
                await Task.WhenAll(cases.Select(async scenario =>
                {
                    await gate.WaitAsync(cancellationToken);
                    try
                    {
                        var (artifacts, removed) = await Task.Run(() => DiagnosticsEvaluationRunner.ExecuteCase(scenario,
                            snapshotDirs[scenario.Request.Snapshot.ContentHash].Dir, 1, executor, options, key,
                            Path.Combine(outputRoot, ".staged-snapshots", $"{scenario.CaseId}-i{current}"),
                            $"{scenario.CaseId}-i{current}-{Guid.NewGuid():N}", cancellationToken), cancellationToken);
                        var replay = await DiagnosticsEvaluationRunner.WriteAndReplayAsync(artifacts, $"{scenario.CaseId}-i{current}",
                            Path.Combine(outputRoot, $"iteration-{current:D3}", scenario.CaseId), key, stderr, cancellationToken);
                        var metrics = DiagnosticsEvaluationRunner.Measure(scenario, artifacts, replay, true, removed);
                        results[(current, scenario.CaseId)] = (metrics, Signature(metrics, artifacts.Outcome));
                    }
                    finally { gate.Release(); }
                }));
                await stdout.WriteLineAsync($"Iteration {iteration}/{runs}: {cases.Count(c => results[(current, c.CaseId)].Metrics.TaskSuccess)}/{cases.Length} cases succeeded");
            }

            var report = Build(executor, runs, concurrency, cases, results, baselineRuns, slack, threshold);
            await File.WriteAllTextAsync(Path.Combine(outputRoot, "repeat-report.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions(RepositoryToolExecutor.JsonOptions) { WriteIndented = true }) + "\n", cancellationToken);
            foreach (var check in report.GateChecks) await stdout.WriteLineAsync($"  {(check.Passed ? "PASS" : "FAIL")} {check.Name}: {check.Detail}");
            foreach (var series in report.Drift.Where(s => !s.Gating))
                await stdout.WriteLineAsync($"  INFO {series.Name}: {(series.AlarmIteration is null ? "no drift alarm" : $"CUSUM alarm at iteration {series.AlarmIteration}")} (not gating)");
            await stdout.WriteLineAsync($"Repeat gate: {(report.Passed ? "PASS" : "FAIL")}; {report.TotalRuns} runs, concurrency {concurrency}.");
            return report.Passed ? 0 : 1;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or RepositoryPolicyException or InvalidOperationException or JsonException)
        {
            await stderr.WriteLineAsync(ex.Message);
            return 2;
        }
    }

    public static DiagnosticsRepeatReport Build(string executor, int runs, int concurrency, IReadOnlyList<DiagnosticsEvaluationCase> cases,
        IReadOnlyDictionary<(int Iteration, string CaseId), (DiagnosticsCaseMetrics Metrics, string Signature)> results,
        int baselineRuns, double slack, double threshold)
    {
        var caseRows = cases.Select(c =>
        {
            var rows = Enumerable.Range(1, runs).Select(i => results[(i, c.CaseId)]).ToArray();
            var signatures = rows.Select(r => r.Signature).Distinct().ToArray();
            var stageFailures = DiagnosticsEvaluationRunner.StageNames.ToDictionary(name => name,
                name => rows.Count(r => r.Metrics.Stages is not null && !DiagnosticsEvaluationRunner.StageValue(r.Metrics.Stages, name)));
            return new DiagnosticsRepeatCase(c.CaseId, runs, rows.Count(r => r.Metrics.TaskSuccess), signatures.Length, signatures.Length == 1,
                rows.Count(r => !r.Metrics.ReplayEquivalent), stageFailures, signatures[0]);
        }).ToArray();
        var iterations = Enumerable.Range(1, runs).Select(i =>
        {
            var rows = cases.Select(c => results[(i, c.CaseId)].Metrics).ToArray();
            return new DiagnosticsIterationResult(i, rows.Count(m => m.TaskSuccess), rows.Length,
                rows.Sum(m => m.Stages is null ? 0 : DiagnosticsEvaluationRunner.StageNames.Count(n => !DiagnosticsEvaluationRunner.StageValue(m.Stages, n))),
                Math.Round(DiagnosticsDrift.Percentile(rows.Select(m => m.PlannerOverheadMs), 0.5), 4),
                Math.Round(DiagnosticsDrift.Percentile(rows.Select(m => m.ToolDurationMs), 0.5), 4));
        }).ToArray();
        var all = results.Values.Select(r => r.Metrics).ToArray();
        var latency = new DiagnosticsRepeatLatency(
            DiagnosticsDrift.Summarize(all.Select(m => m.PlannerOverheadMs).ToArray()),
            DiagnosticsDrift.Summarize(all.Select(m => m.ToolDurationMs).ToArray()),
            DiagnosticsDrift.Summarize(all.Select(m => m.SandboxOverheadMs).ToArray()));
        var drift = new[]
        {
            DiagnosticsDrift.StageFailureDrift(iterations),
            DiagnosticsDrift.LatencyDrift("tool-latency", iterations.Select(i => i.MedianToolMs).ToArray(), baselineRuns, slack, threshold),
            DiagnosticsDrift.LatencyDrift("planner-overhead", iterations.Select(i => i.MedianPlannerMs).ToArray(), baselineRuns, slack, threshold)
        };
        var unstable = caseRows.Where(c => !c.Stable).Select(c => c.CaseId).ToArray();
        var checks = new List<DiagnosticsGateCheck>
        {
            new("run-stability", unstable.Length == 0, unstable.Length == 0 ? $"{caseRows.Length} cases produced one outcome signature across {runs} runs" : "unstable: " + string.Join(", ", unstable)),
            new("task-success", caseRows.All(c => c.Successes == runs), $"{caseRows.Sum(c => c.Successes)}/{caseRows.Length * runs} runs succeeded"),
            new("replay-equivalence", caseRows.All(c => c.ReplayFailures == 0), $"{caseRows.Sum(c => runs - c.ReplayFailures)}/{caseRows.Length * runs} runs replayed"),
            new("stage-drift", drift[0].AlarmIteration is null, drift[0].AlarmIteration is null ? "no CUSUM alarm" : $"CUSUM alarm at iteration {drift[0].AlarmIteration}")
        };
        return new("0.1", executor, runs, concurrency, runs * cases.Count, caseRows, iterations, latency, drift, checks, checks.All(c => c.Passed));
    }

    // Excludes run ids, invocation ids, timestamps and latency: only what must be identical across repeated deterministic runs.
    public static string Signature(DiagnosticsCaseMetrics metrics, DiagnosticsOutcome outcome)
    {
        var stable = new
        {
            outcome.Status, outcome.ReasonCode, outcome.Attempts, outcome.Diagnosis, outcome.NarrowingCandidates,
            Evidence = outcome.Evidence.Select(e => new { e.Kind, e.Path, e.Line, e.ContentHash }),
            metrics.TaskSuccess, metrics.ExactScore, metrics.SemanticScore, metrics.ScoreFailures, metrics.Stages, metrics.ToolCalls, metrics.Confidence
        };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(stable, RepositoryToolExecutor.JsonOptions))));
    }

    private static int IntOption(Dictionary<string, string> options, string name, int fallback, int min, int max)
    {
        var value = options.TryGetValue(name, out var text) ? int.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : fallback;
        return value >= min && value <= max ? value : throw new ArgumentException($"{name} must be between {min} and {max}.");
    }

    private static double DoubleOption(Dictionary<string, string> options, string name, double fallback)
    {
        var value = options.TryGetValue(name, out var text) ? double.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : fallback;
        return value > 0 && double.IsFinite(value) ? value : throw new ArgumentException($"{name} must be a positive number.");
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
            if (index + 1 >= args.Length || !Known.Contains(args[index]) || !result.TryAdd(args[index], args[index + 1]))
                throw new ArgumentException("Usage: repeat-diagnostics --snapshot-dir <dir> --cases-root <dir> --output-dir <dir> --hmac-key <key> [--executor container-v1|process-v1] [--worker-assembly <dll>] [--snapshot-dir-v2 <dir>] [--runs <n>] [--concurrency <n>] [--baseline-runs <n>] [--latency-slack <fraction>] [--latency-threshold <fraction>]");
        foreach (var key in Known.Take(4)) if (!result.ContainsKey(key)) throw new ArgumentException($"Missing {key}.");
        return result;
    }
}
