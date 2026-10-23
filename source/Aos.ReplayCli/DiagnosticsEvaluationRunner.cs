using System.Text.Json;
using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Aos.WebApi.Services;
using Microsoft.Extensions.Options;

namespace Aos.ReplayCli;

public static class DiagnosticsEvaluationRunner
{
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        try
        {
            var options = Parse(args);
            var snapshot = RepositoryDiagnosticsDefaults.ImportFixture(options["--snapshot-dir"]);
            // Each case runs against the directory whose content hash it pins; v1 stays the default fixture.
            var snapshotDirs = new Dictionary<string, (string Dir, RepositorySnapshot Snapshot)>(StringComparer.Ordinal)
                { [snapshot.Reference.ContentHash] = (options["--snapshot-dir"], snapshot) };
            if (options.TryGetValue("--snapshot-dir-v2", out var v2Dir))
            {
                var second = RepositoryDiagnosticsDefaults.ImportFixture(v2Dir);
                if (!snapshotDirs.TryAdd(second.Reference.ContentHash, (v2Dir, second))) throw new ArgumentException("Snapshot directories must differ.");
            }
            var minCases = options.TryGetValue("--min-cases", out var minCasesText) ? int.Parse(minCasesText, System.Globalization.CultureInfo.InvariantCulture) : 12;
            var minSuccesses = options.TryGetValue("--min-successes", out var minSuccessText) ? int.Parse(minSuccessText, System.Globalization.CultureInfo.InvariantCulture) : 10;
            var executor = options.GetValueOrDefault("--executor", "container-v1");
            if (executor is not ("container-v1" or "process-v1")) throw new ArgumentException("Executor must be container-v1 or process-v1.");
            var cases = Directory.EnumerateFiles(options["--cases-root"], "*.json").Order(StringComparer.Ordinal)
                .Select(file => JsonSerializer.Deserialize<DiagnosticsEvaluationCase>(File.ReadAllText(file), RepositoryToolExecutor.JsonOptions)
                    ?? throw new ArgumentException("Invalid case file.")).ToArray();
            if (cases.Length == 0 || cases.Select(c => c.CaseId).Distinct().Count() != cases.Length)
                throw new ArgumentException("Dataset must contain unique versioned cases.");
            foreach (var scenario in cases)
            {
                if (string.IsNullOrWhiteSpace(scenario.CaseId) || scenario.CaseId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
                    throw new ArgumentException("Case ids must contain only ASCII letters, digits, and hyphens.");
                if (!snapshotDirs.TryGetValue(scenario.Request.Snapshot.ContentHash, out var pinned))
                    throw new ArgumentException($"{scenario.CaseId} pins a snapshot with no matching --snapshot-dir.");
                pinned.Snapshot.Verify(scenario.Request.Snapshot);
            }
            var outputRoot = Path.GetFullPath(options["--output-dir"]);
            Directory.CreateDirectory(outputRoot);
            var v1 = new List<DiagnosticsCaseMetrics>();
            var v2 = new List<DiagnosticsCaseMetrics>();
            var key = options["--hmac-key"];
            foreach (var retries in new[] { 0, 1 })
            {
                foreach (var scenario in cases)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // RDI-12 runs from a disposable copy that is deleted before replay, proving replay needs no snapshot.
                    var caseDir = snapshotDirs[scenario.Request.Snapshot.ContentHash].Dir;
                    var staged = scenario.ReplayWithoutSnapshot
                        ? StageSnapshot(caseDir, Path.Combine(outputRoot, ".staged-snapshots", $"{scenario.CaseId}-v{retries + 1}"), scenario.Request.Snapshot)
                        : null;
                    var config = new SandboxPoolOptions { PoolSize = 0, ExecutorType = executor,
                        RepositorySnapshotDirectory = staged ?? Path.GetFullPath(caseDir),
                        ContainerImage = options.GetValueOrDefault("--container-image", "aos-sandbox-worker:local") };
                    Func<ISandboxSlot> factory = executor == "container-v1" ? SandboxSlotFactory.Create(config) :
                        () => new ProcessSandboxSlot(options.GetValueOrDefault("--worker-assembly"), environmentVariables:
                            new Dictionary<string, string> { ["AOS_REPOSITORY_SNAPSHOT_ROOT"] = config.RepositorySnapshotDirectory! });
                    using var pool = new PreWarmedSandboxPool(0, factory);
                    var capability = new HmacJwtCapabilityTokenService(Options.Create(new CapabilityTokenOptions
                    {
                        Issuer = "diagnostics-evaluation", Audience = "diagnostics-tools",
                        SigningKey = "evaluation-capability-key-local-32-bytes", KeyId = "evaluation-capability"
                    }));
                    var catalog = RepositoryDiagnosticsDefaults.Catalog();
                    var clock = new SystemTimeSource();
                    var service = new DiagnosticsWorkflowService(RepositoryDiagnosticsDefaults.Planner(catalog), RepositoryDiagnosticsDefaults.Policies(),
                        catalog, new PlannerStepExecutor(catalog, capability,
                            new CapabilityEnforcingToolExecutor(capability, new PooledSandboxToolExecutor(pool, Options.Create(config))), clock),
                        new RuleBasedDiagnosticsDecisions(), RepositoryDiagnosticsDefaults.Recovery(retries), new LockedSeedProvider(new RandomSeedGenerator()),
                        clock, new DiagnosticsRuleRouter(), new HmacEventLogIntegrityChain(key, "diagnostics-evaluation"), new HmacManifestSigner(key, "diagnostics-evaluation"));
                    var artifacts = service.CreateArtifacts($"{scenario.CaseId}-v{retries + 1}-{Guid.NewGuid():N}", scenario.Request, cancellationToken);
                    bool? snapshotRemoved = null;
                    if (staged is not null)
                    {
                        pool.Dispose();
                        Directory.Delete(staged, recursive: true);
                        snapshotRemoved = !Directory.Exists(staged);
                    }
                    var dir = Path.Combine(outputRoot, $"planner-v{retries + 1}", scenario.CaseId);
                    Directory.CreateDirectory(dir);
                    await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(artifacts.ManifestRecord, RepositoryToolExecutor.JsonOptions) + "\n", cancellationToken);
                    await File.WriteAllTextAsync(Path.Combine(dir, "eventlog.jsonl"), string.Concat(artifacts.EventLogRecords.Select(r => JsonSerializer.Serialize(r, RepositoryToolExecutor.JsonOptions) + "\n")), cancellationToken);
                    await File.WriteAllTextAsync(Path.Combine(dir, "scenario.json"), JsonSerializer.Serialize(new
                    { name = $"{scenario.CaseId}-planner-v{retries + 1}", workflow = "diagnostics", manifestPath = "manifest.json", eventLogPath = "eventlog.jsonl" }, RepositoryToolExecutor.JsonOptions), cancellationToken);
                    using var replayOut = new StringWriter();
                    using var replayError = new StringWriter();
                    var replay = await ReplayCliRunner.RunAsync(["--workflow", "diagnostics", "--artifact-dir", dir, "--hmac-key", key], replayOut, replayError, cancellationToken) == 0;
                    var metrics = Measure(scenario, artifacts, replay, retries == 1, snapshotRemoved);
                    (retries == 0 ? v1 : v2).Add(metrics);
                    await stdout.WriteLineAsync($"Planner v{retries + 1} {scenario.CaseId}: {(metrics.TaskSuccess ? "PASS" : "FAIL")} {metrics.Outcome} ({metrics.ReasonCode}), replay={(replay ? "PASS" : "FAIL")}");
                    if (!replay) await stderr.WriteLineAsync(replayError.ToString());
                }
            }
            var improvement = cases.Where(c => c.Recoverable).Sum(c =>
                (v2.Single(m => m.CaseId == c.CaseId).TaskSuccess ? 1 : 0) - (v1.Single(m => m.CaseId == c.CaseId).TaskSuccess ? 1 : 0));
            var regression = cases.Where(c => !c.Recoverable).Any(c => v1.Single(m => m.CaseId == c.CaseId).TaskSuccess && !v2.Single(m => m.CaseId == c.CaseId).TaskSuccess);
            var regressions = options.TryGetValue("--baseline", out var baselinePath)
                ? CompareToBaseline(JsonSerializer.Deserialize<DiagnosticsEvaluationReport>(File.ReadAllText(baselinePath), RepositoryToolExecutor.JsonOptions)
                    ?? throw new ArgumentException("Invalid baseline report."), v2) : [];
            var checks = new List<DiagnosticsGateCheck>
            {
                new("dataset-size", cases.Length >= minCases, $"{cases.Length} cases, minimum {minCases}"),
                new("task-success", v2.Count(m => m.TaskSuccess) >= minSuccesses, $"{v2.Count(m => m.TaskSuccess)} of {cases.Length} succeed, minimum {minSuccesses}"),
                new("allowed-action-validity", v2.All(m => m.AllowedActionsValid), $"{v2.Count(m => m.AllowedActionsValid)}/{v2.Count} cases used catalogue actions only"),
                new("prohibited-action-rejection", v2.All(m => m.ProhibitedActionRejected != false), $"{v2.Count(m => m.ProhibitedActionRejected == true)}/{v2.Count(m => m.ProhibitedActionRejected is not null)} prohibited cases rejected"),
                new("evidence-grounding", v2.All(m => m.EvidenceGroundingPassed), $"{v2.Count(m => m.EvidenceGroundingPassed)}/{v2.Count} cases grounded"),
                new("replay-equivalence", v1.Concat(v2).All(m => m.ReplayEquivalent && m.SnapshotRemovedBeforeReplay != false), $"{v1.Concat(v2).Count(m => m.ReplayEquivalent)}/{v1.Count + v2.Count} runs replayed"),
                new("recovery-improvement", improvement > 0 && !regression, $"improvement {improvement}, non-recovery regression {regression}"),
                new("baseline-regressions", regressions.Count == 0, regressions.Count == 0 ? "none" : string.Join("; ", regressions))
            };
            var passed = checks.All(c => c.Passed);
            var report = new DiagnosticsEvaluationReport(DiagnosticsSchema.Version, executor, snapshot.Reference, v1, v2,
                v1.Count(m => m.TaskSuccess), v2.Count(m => m.TaskSuccess), improvement, regression, passed,
                snapshotDirs.Values.Select(d => d.Snapshot.Reference).ToArray(), new(minCases, minSuccesses), checks, new(Summarize(v1), Summarize(v2)), regressions);
            await File.WriteAllTextAsync(Path.Combine(outputRoot, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions(RepositoryToolExecutor.JsonOptions) { WriteIndented = true }) + "\n", cancellationToken);
            foreach (var check in checks) await stdout.WriteLineAsync($"  {(check.Passed ? "PASS" : "FAIL")} {check.Name}: {check.Detail}");
            await stdout.WriteLineAsync($"Diagnostics gate: {(passed ? "PASS" : "FAIL")}; v1={report.V1Successes}/{cases.Length}, v2={report.V2Successes}/{cases.Length}; recoverable improvement={improvement}; non-recovery regression={regression}.");
            return passed ? 0 : 1;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or RepositoryPolicyException or InvalidOperationException or JsonException)
        {
            await stderr.WriteLineAsync(ex.Message);
            return 2;
        }
    }

    public static DiagnosticsCaseMetrics Measure(DiagnosticsEvaluationCase expected, DiagnosticsWorkflowArtifacts artifacts, bool replay, bool v2,
        bool? snapshotRemovedBeforeReplay = null)
    {
        var plans = Read<DiagnosticsPlanEvent>(artifacts, "diagnostics.plan");
        var tools = Read<ToolExecutionEvent>(artifacts, "tool.execution");
        var critics = Read<DiagnosticsCriticDecision>(artifacts, "diagnostics.critic");
        var recoveries = Read<DiagnosticsRecoveryDecision>(artifacts, "diagnostics.recovery");
        var outcome = artifacts.Outcome;
        var grounding = outcome.Status != "succeeded" || outcome.Evidence.Count > 0 && outcome.Evidence.All(e =>
            tools.Any(t => t.InvocationId == e.InvocationId && t.Status == "succeeded"));
        // Plan rejection must stop before any tool call; tool-level denials must carry the case's stable policy code.
        bool? prohibited = expected.ExpectedStatus == "denied" ? outcome.Status == "denied" &&
            (expected.ExpectedDenialCode == "planner.plan.invalid"
                ? tools.Count == 0 && plans.All(p => !p.IsValid && p.ErrorCode == expected.ExpectedDenialCode)
                : expected.ExpectedDenialCode is not null && tools.Any(t => t.Status == "denied" && t.Error == expected.ExpectedDenialCode)) : null;
        var score = DiagnosticsCaseScorer.Evaluate(expected, outcome, tools, v2);
        var catalogue = RepositoryDiagnosticsDefaults.Catalog().Actions.Select(a => a.ActionId).ToHashSet(StringComparer.Ordinal);
        var actionsValid = tools.All(t => catalogue.Contains(t.ToolId)) &&
            plans.Where(p => p.Plan is not null).All(p => p.Plan!.Steps.All(step => catalogue.Contains(step.ActionId)));
        var finalTools = FinalAttemptTools(artifacts);
        var stages = Stages(expected, outcome, plans, finalTools, score, grounding, replay, snapshotRemovedBeforeReplay, v2);
        return new(expected.CaseId, score.Passed,
            plans.Count == 0 ? 0 : (double)plans.Count(p => p.IsValid) / plans.Count, grounding,
            tools.Count == 0 ? 0 : (double)tools.Count(t => t.Status == "succeeded") / tools.Count,
            critics.Where(c => c.Decision != "accept").Select(c => c.ReasonCode).ToArray(),
            recoveries.Count(r => r.NextTask is not null), outcome.Status == "succeeded" && outcome.Attempts > 1,
            prohibited, replay, artifacts.Timing?.PlannerOverheadMs ?? 0, artifacts.Timing?.ToolDurationMs ?? 0,
            artifacts.Timing?.SandboxOverheadMs ?? 0, outcome.Status, outcome.ReasonCode, snapshotRemovedBeforeReplay,
            score.ExactScore, score.SemanticScore, score.Failures, actionsValid, stages,
            tools.Select((t, index) => new DiagnosticsToolCallRow(index + 1, t.ToolId, t.Status, t.Error)).ToArray());
    }

    // Tool events recorded after the last plan event belong to the final attempt.
    private static IReadOnlyList<ToolExecutionEvent> FinalAttemptTools(DiagnosticsWorkflowArtifacts artifacts)
    {
        var final = new List<ToolExecutionEvent>();
        foreach (var record in artifacts.EventLogRecords)
        {
            if (record.Entry.EventType == "diagnostics.plan") final.Clear();
            else if (record.Entry.EventType == "tool.execution")
                final.Add(JsonSerializer.SerializeToElement(record.Entry.Data, RepositoryToolExecutor.JsonOptions).Deserialize<ToolExecutionEvent>(RepositoryToolExecutor.JsonOptions)!);
        }
        return final;
    }

    // Planning -> tool -> evidence fail fast: only the first failing stage is attributed, later ones are not reached.
    private static DiagnosticsStageResults Stages(DiagnosticsEvaluationCase expected, DiagnosticsOutcome outcome,
        IReadOnlyList<DiagnosticsPlanEvent> plans, IReadOnlyList<ToolExecutionEvent> finalTools, DiagnosticsScore score,
        bool grounding, bool replay, bool? snapshotRemoved, bool v2)
    {
        var planInvalid = expected.ExpectedDenialCode == "planner.plan.invalid";
        var planning = plans.Count > 0 && plans[^1].IsValid == !planInvalid;
        var tool = !planning || (planInvalid || expected.ExpectedStatus == "inconclusive" ? finalTools.Count == 0 :
            expected.ExpectedDenialCode is not null ? finalTools.Any(t => t.Status == "denied" && t.Error == expected.ExpectedDenialCode) :
            finalTools.Count > 0 && finalTools.All(t => t.Status == "succeeded"));
        var evidence = !planning || !tool || (expected.ExpectedStatus == "succeeded"
            ? outcome.Status == "succeeded" && grounding && !score.Failures.Any(f => f.StartsWith("evidence", StringComparison.Ordinal) ||
                f.StartsWith("concept", StringComparison.Ordinal) || f.StartsWith("forbidden", StringComparison.Ordinal) ||
                f.StartsWith("fact", StringComparison.Ordinal) || f.StartsWith("scope", StringComparison.Ordinal) || f.StartsWith("diagnosis", StringComparison.Ordinal))
            : outcome.ReasonCode == expected.ExpectedReason && outcome.Evidence.Count == 0);
        return new(planning, tool, evidence, outcome.Attempts == (v2 ? expected.ExpectedAttempts : 1), replay && snapshotRemoved != false);
    }

    private static readonly string[] StageNames = ["planning", "tool", "evidence", "recovery", "replay"];
    private static IReadOnlyDictionary<string, DiagnosticsStageCount> Summarize(IReadOnlyList<DiagnosticsCaseMetrics> metrics) =>
        StageNames.ToDictionary(name => name, name =>
        {
            var failed = metrics.Where(m => m.Stages is not null && !StageValue(m.Stages, name)).Select(m => m.CaseId).ToArray();
            return new DiagnosticsStageCount(metrics.Count - failed.Length, failed.Length, failed);
        });
    private static bool StageValue(DiagnosticsStageResults stages, string name) => name switch
    {
        "planning" => stages.Planning, "tool" => stages.Tool, "evidence" => stages.Evidence, "recovery" => stages.Recovery, _ => stages.Replay
    };

    // A regression is a Planner v2 case or stage that passed in the baseline report and fails now.
    public static IReadOnlyList<string> CompareToBaseline(DiagnosticsEvaluationReport baseline, IReadOnlyList<DiagnosticsCaseMetrics> current)
    {
        var found = new List<string>();
        foreach (var before in baseline.PlannerV2)
        {
            var now = current.SingleOrDefault(m => m.CaseId == before.CaseId);
            if (now is null) { found.Add($"{before.CaseId}: missing from the current run"); continue; }
            if (before.TaskSuccess && !now.TaskSuccess) found.Add($"{before.CaseId}: task success regressed");
            if (before.Stages is not null && now.Stages is not null)
                foreach (var name in StageNames)
                    if (StageValue(before.Stages, name) && !StageValue(now.Stages, name)) found.Add($"{before.CaseId}: {name} stage regressed");
        }
        return found;
    }

    private static string StageSnapshot(string source, string target, RepositorySnapshotRef expected)
    {
        source = Path.GetFullPath(source);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        RepositoryDiagnosticsDefaults.ImportFixture(target).Verify(expected);
        return target;
    }

    private static IReadOnlyList<T> Read<T>(DiagnosticsWorkflowArtifacts artifacts, string type) =>
        artifacts.EventLogRecords.Where(r => r.Entry.EventType == type).Select(r =>
            JsonSerializer.SerializeToElement(r.Entry.Data, RepositoryToolExecutor.JsonOptions).Deserialize<T>(RepositoryToolExecutor.JsonOptions)!).ToArray();
    private static Dictionary<string, string> Parse(string[] args)
    {
        var known = new[] { "--snapshot-dir", "--cases-root", "--output-dir", "--hmac-key", "--executor", "--worker-assembly", "--container-image", "--snapshot-dir-v2", "--baseline", "--min-cases", "--min-successes" };
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
            if (index + 1 >= args.Length || !known.Contains(args[index]) || !result.TryAdd(args[index], args[index + 1]))
                throw new ArgumentException("Usage: evaluate-diagnostics --snapshot-dir <dir> --cases-root <dir> --output-dir <dir> --hmac-key <key> [--executor container-v1|process-v1] [--worker-assembly <dll>] [--snapshot-dir-v2 <dir>] [--baseline <report.json>] [--min-cases <n>] [--min-successes <n>]");
        foreach (var key in known.Take(4)) if (!result.ContainsKey(key)) throw new ArgumentException($"Missing {key}.");
        return result;
    }
}
