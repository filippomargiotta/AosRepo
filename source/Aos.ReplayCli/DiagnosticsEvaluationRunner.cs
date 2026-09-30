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
            var snapshot = RepositorySnapshot.Import(options["--snapshot-dir"], RepositoryDiagnosticsDefaults.Revision, RepositoryDiagnosticsDefaults.CreatedAt);
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
                snapshot.Verify(scenario.Request.Snapshot);
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
                    var config = new SandboxPoolOptions { PoolSize = 0, ExecutorType = executor,
                        RepositorySnapshotDirectory = Path.GetFullPath(options["--snapshot-dir"]),
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
                    var dir = Path.Combine(outputRoot, $"planner-v{retries + 1}", scenario.CaseId);
                    Directory.CreateDirectory(dir);
                    await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(artifacts.ManifestRecord, RepositoryToolExecutor.JsonOptions) + "\n", cancellationToken);
                    await File.WriteAllTextAsync(Path.Combine(dir, "eventlog.jsonl"), string.Concat(artifacts.EventLogRecords.Select(r => JsonSerializer.Serialize(r, RepositoryToolExecutor.JsonOptions) + "\n")), cancellationToken);
                    await File.WriteAllTextAsync(Path.Combine(dir, "scenario.json"), JsonSerializer.Serialize(new
                    { name = $"{scenario.CaseId}-planner-v{retries + 1}", workflow = "diagnostics", manifestPath = "manifest.json", eventLogPath = "eventlog.jsonl" }, RepositoryToolExecutor.JsonOptions), cancellationToken);
                    using var replayOut = new StringWriter();
                    using var replayError = new StringWriter();
                    var replay = await ReplayCliRunner.RunAsync(["--workflow", "diagnostics", "--artifact-dir", dir, "--hmac-key", key], replayOut, replayError, cancellationToken) == 0;
                    var metrics = Measure(scenario, artifacts, replay, retries == 1);
                    (retries == 0 ? v1 : v2).Add(metrics);
                    await stdout.WriteLineAsync($"Planner v{retries + 1} {scenario.CaseId}: {(metrics.TaskSuccess ? "PASS" : "FAIL")} {metrics.Outcome} ({metrics.ReasonCode}), replay={(replay ? "PASS" : "FAIL")}");
                    if (!replay) await stderr.WriteLineAsync(replayError.ToString());
                }
            }
            var improvement = cases.Where(c => c.Recoverable).Sum(c =>
                (v2.Single(m => m.CaseId == c.CaseId).TaskSuccess ? 1 : 0) - (v1.Single(m => m.CaseId == c.CaseId).TaskSuccess ? 1 : 0));
            var regression = cases.Where(c => !c.Recoverable).Any(c => v1.Single(m => m.CaseId == c.CaseId).TaskSuccess && !v2.Single(m => m.CaseId == c.CaseId).TaskSuccess);
            var passed = v2.All(m => m.TaskSuccess && m.ReplayEquivalent && m.EvidenceGroundingPassed && m.ProhibitedActionRejected != false) &&
                v1.All(m => m.ReplayEquivalent) && improvement > 0 && !regression;
            var report = new DiagnosticsEvaluationReport(DiagnosticsSchema.Version, executor, snapshot.Reference, v1, v2,
                v1.Count(m => m.TaskSuccess), v2.Count(m => m.TaskSuccess), improvement, regression, passed);
            await File.WriteAllTextAsync(Path.Combine(outputRoot, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions(RepositoryToolExecutor.JsonOptions) { WriteIndented = true }) + "\n", cancellationToken);
            await stdout.WriteLineAsync($"September gate: {(passed ? "PASS" : "FAIL")}; v1={report.V1Successes}/{cases.Length}, v2={report.V2Successes}/{cases.Length}; recoverable improvement={improvement}; non-recovery regression={regression}.");
            return passed ? 0 : 1;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or RepositoryPolicyException or InvalidOperationException or JsonException)
        {
            await stderr.WriteLineAsync(ex.Message);
            return 2;
        }
    }

    public static DiagnosticsCaseMetrics Measure(DiagnosticsEvaluationCase expected, DiagnosticsWorkflowArtifacts artifacts, bool replay, bool v2)
    {
        var plans = Read<DiagnosticsPlanEvent>(artifacts, "diagnostics.plan");
        var tools = Read<ToolExecutionEvent>(artifacts, "tool.execution");
        var critics = Read<DiagnosticsCriticDecision>(artifacts, "diagnostics.critic");
        var recoveries = Read<DiagnosticsRecoveryDecision>(artifacts, "diagnostics.recovery");
        var outcome = artifacts.Outcome;
        var grounding = outcome.Status != "succeeded" || outcome.Evidence.Count > 0 && outcome.Evidence.All(e =>
            tools.Any(t => t.InvocationId == e.InvocationId && t.Status == "succeeded"));
        bool? prohibited = expected.ExpectedStatus == "denied" ? outcome.Status == "denied" &&
            (expected.CaseId == "RDI-10" ? tools.Count == 0 && plans.All(p => !p.IsValid) : tools.Any(t => t.Status == "denied" && t.Error == "repo.path.denied")) : null;
        return new(expected.CaseId, DiagnosticsCaseScorer.Score(expected, outcome, tools, v2),
            plans.Count == 0 ? 0 : (double)plans.Count(p => p.IsValid) / plans.Count, grounding,
            tools.Count == 0 ? 0 : (double)tools.Count(t => t.Status == "succeeded") / tools.Count,
            critics.Where(c => c.Decision != "accept").Select(c => c.ReasonCode).ToArray(),
            recoveries.Count(r => r.NextTask is not null), outcome.Status == "succeeded" && outcome.Attempts > 1,
            prohibited, replay, artifacts.Timing?.PlannerOverheadMs ?? 0, artifacts.Timing?.ToolDurationMs ?? 0,
            artifacts.Timing?.SandboxOverheadMs ?? 0, outcome.Status, outcome.ReasonCode);
    }

    private static IReadOnlyList<T> Read<T>(DiagnosticsWorkflowArtifacts artifacts, string type) =>
        artifacts.EventLogRecords.Where(r => r.Entry.EventType == type).Select(r =>
            JsonSerializer.SerializeToElement(r.Entry.Data, RepositoryToolExecutor.JsonOptions).Deserialize<T>(RepositoryToolExecutor.JsonOptions)!).ToArray();
    private static Dictionary<string, string> Parse(string[] args)
    {
        var known = new[] { "--snapshot-dir", "--cases-root", "--output-dir", "--hmac-key", "--executor", "--worker-assembly", "--container-image" };
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
            if (index + 1 >= args.Length || !known.Contains(args[index]) || !result.TryAdd(args[index], args[index + 1]))
                throw new ArgumentException("Usage: evaluate-diagnostics --snapshot-dir <dir> --cases-root <dir> --output-dir <dir> --hmac-key <key> [--executor container-v1|process-v1] [--worker-assembly <dll>]");
        foreach (var key in known.Take(4)) if (!result.ContainsKey(key)) throw new ArgumentException($"Missing {key}.");
        return result;
    }
}
