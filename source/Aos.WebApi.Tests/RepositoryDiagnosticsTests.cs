using System.Text.Json;
using Aos.ReplayCli;
using Aos.WebApi.Models;
using Aos.WebApi.Services;
using Xunit;

namespace Aos.WebApi.Tests;

public sealed class RepositoryDiagnosticsTests
{
    private static readonly DateTimeOffset Instant = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    internal static string SnapshotRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Aos.RepositoryFixture", "Repository"));
    internal static string CasesRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "DiagnosticsCases"));
    private static RepositorySnapshot Snapshot() => RepositorySnapshot.Import(SnapshotRoot, RepositoryDiagnosticsDefaults.Revision, RepositoryDiagnosticsDefaults.CreatedAt);
    internal static IReadOnlyList<DiagnosticsEvaluationCase> Cases() => Directory.EnumerateFiles(CasesRoot, "*.json").Order(StringComparer.Ordinal)
        .Select(p => JsonSerializer.Deserialize<DiagnosticsEvaluationCase>(File.ReadAllText(p), RepositoryToolExecutor.JsonOptions)!).ToArray();

    internal static DiagnosticsWorkflowArtifacts Run(DiagnosticsEvaluationCase scenario, int retries = 1,
        IDiagnosticsDecisions? decisions = null, IPlannerService? planner = null, IToolExecutor? tools = null)
    {
        var snapshot = Snapshot();
        snapshot.Verify(scenario.Request.Snapshot);
        var catalog = RepositoryDiagnosticsDefaults.Catalog();
        var capabilities = CapabilityTestData.CreateTokenService();
        tools ??= new RepositoryToolExecutor(snapshot,
            RepositoryDiagnosticsDefaults.Profiles(snapshot.Reference.ContentHash, Path.Combine(AppContext.BaseDirectory, "Aos.SandboxWorker.dll")),
            new PackagedRepositoryTestRunner());
        var clock = new FixedTimeSource(Instant, new("record", "golden-fixed", "diagnostics-golden-clock", "utc-millis", "September fixture"));
        var workflow = new DiagnosticsWorkflowService(planner ?? RepositoryDiagnosticsDefaults.Planner(catalog), RepositoryDiagnosticsDefaults.Policies(), catalog,
            new PlannerStepExecutor(catalog, capabilities, new CapabilityEnforcingToolExecutor(capabilities, tools), clock),
            decisions ?? new RuleBasedDiagnosticsDecisions(), RepositoryDiagnosticsDefaults.Recovery(retries),
            new FixedSeedProvider(new("fixture-seed", "test-sequence", 930, "golden-fixed")), clock, new DiagnosticsRuleRouter(),
            new HmacEventLogIntegrityChain(GoldenArtifactTestSupport.GoldenHmacKey, GoldenArtifactTestSupport.GoldenHmacKeyId),
            new HmacManifestSigner(GoldenArtifactTestSupport.GoldenHmacKey, GoldenArtifactTestSupport.GoldenHmacKeyId));
        return workflow.CreateArtifacts($"run-golden-{scenario.CaseId.ToLowerInvariant()}", scenario.Request);
    }

    internal static IReadOnlyList<T> Events<T>(DiagnosticsWorkflowArtifacts artifacts, string type) =>
        artifacts.EventLogRecords.Where(r => r.Entry.EventType == type).Select(r => JsonSerializer.SerializeToElement(r.Entry.Data,
            RepositoryToolExecutor.JsonOptions).Deserialize<T>(RepositoryToolExecutor.JsonOptions)!).ToArray();

    [Fact]
    public async Task VersionedCases_ImproveRecoveryAndReplayWithoutLiveDependencies()
    {
        var v1 = new List<DiagnosticsCaseMetrics>();
        var v2 = new List<DiagnosticsCaseMetrics>();
        var cases = Cases();
        Assert.Equal(["RDI-01", "RDI-02", "RDI-06", "RDI-07", "RDI-08", "RDI-09", "RDI-10", "RDI-11", "RDI-12"], cases.Select(c => c.CaseId));
        foreach (var scenario in cases)
        {
            var original = Run(scenario, 0);
            var improved = Run(scenario);
            var name = $"diagnostics-{scenario.CaseId.ToLowerInvariant()}-v1";
            UpdateGolden(name, improved);
            Assert.Equal(GoldenArtifactTestSupport.ReadGoldenManifestJson(name), GoldenArtifactTestSupport.SerializeManifestRecord(improved.ManifestRecord));
            Assert.Equal(GoldenArtifactTestSupport.ReadGoldenEventLogJsonl(name), GoldenArtifactTestSupport.SerializeEventLogLines(improved.EventLogRecords));
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exit = await ReplayCliRunner.RunAsync(["--workflow", "diagnostics", "--artifact-dir", GoldenArtifactTestSupport.GetGoldenDir(name),
                "--hmac-key", GoldenArtifactTestSupport.GoldenHmacKey], stdout, stderr, CancellationToken.None);
            Assert.True(exit == 0, stderr.ToString());
            v1.Add(DiagnosticsEvaluationRunner.Measure(scenario, original, true, false));
            v2.Add(DiagnosticsEvaluationRunner.Measure(scenario, improved, true, true));
            Assert.DoesNotContain("eyJ", GoldenArtifactTestSupport.SerializeEventLogLines(improved.EventLogRecords));
            var tokens = Events<ToolExecutionEvent>(improved, "tool.execution").Select(t => t.InvocationId).ToArray();
            Assert.Equal(tokens.Length, tokens.Distinct().Count());
        }
        Assert.Equal(cases.Count(c => !c.Recoverable), v1.Count(m => m.TaskSuccess));
        Assert.Equal(cases.Count, v2.Count(m => m.TaskSuccess));
        Assert.All(v2, m => { Assert.True(m.TaskSuccess); Assert.True(m.EvidenceGroundingPassed); Assert.NotEqual(false, m.ProhibitedActionRejected); });
        Assert.Equal(2, v2.Count(m => m.RecoverySucceeded));
        foreach (var item in v1.Where(m => m.TaskSuccess)) Assert.True(v2.Single(m => m.CaseId == item.CaseId).TaskSuccess);
    }

    [Fact]
    public void ProhibitedWriteAndShellPlans_AreSignedRejectionsAndNeverReachExecutor()
    {
        var spy = new SpyExecutor();
        var artifacts = Run(Cases().Single(c => c.CaseId == "RDI-10"), tools: spy);
        Assert.Equal("denied", artifacts.Outcome.Status);
        Assert.Equal(0, spy.Calls);
        var plan = Assert.Single(Events<DiagnosticsPlanEvent>(artifacts, "diagnostics.plan"));
        Assert.False(plan.IsValid);
        Assert.Null(plan.Plan);
        Assert.Equal("planner.plan.invalid", plan.ErrorCode);
        Assert.True(new HmacEventLogIntegrityChain(GoldenArtifactTestSupport.GoldenHmacKey, GoldenArtifactTestSupport.GoldenHmacKeyId)
            .TryValidateRecords(artifacts.EventLogRecords, out _));
    }

    [Fact]
    public void EquallySupportedCauses_AreInconclusiveWithoutExecutionOrModelOverride()
    {
        var spy = new SpyExecutor();
        var model = new ModelClient("{\"decision\":\"accept\"}");
        var artifacts = Run(Cases().Single(c => c.CaseId == "RDI-08"), decisions: new RuleBasedDiagnosticsDecisions(model), tools: spy);
        Assert.Equal("inconclusive", artifacts.Outcome.Status);
        Assert.Equal("evidence.ambiguous", artifacts.Outcome.ReasonCode);
        Assert.Null(artifacts.Outcome.Diagnosis);
        Assert.Equal(["configuration-trace", "pricing-diagnosis"], artifacts.Outcome.NarrowingCandidates!);
        Assert.Equal(0, spy.Calls);
        Assert.Equal(0, model.Calls);
        var plan = Assert.Single(Events<DiagnosticsPlanEvent>(artifacts, "diagnostics.plan"));
        Assert.True(plan.IsValid);
        Assert.Equal(2, plan.TiedCandidates!.Count);
        Assert.All(plan.TiedCandidates, c => Assert.Equal(plan.Selection!.Score, c.Score));
    }

    [Fact]
    public void RecordedAcceptance_WithTiedCandidates_FailsValidation()
    {
        var accepted = Run(Cases()[0]);
        var planning = Events<DiagnosticsPlanEvent>(accepted, "diagnostics.plan").Single();
        var critic = Events<DiagnosticsCriticDecision>(accepted, "diagnostics.critic").Single();
        var tied = planning with { TiedCandidates = [planning.Selection!, planning.Selection! with { PlaybookId = "configuration-trace" }] };
        var error = Assert.Throws<InvalidOperationException>(() =>
            DiagnosticsWorkflowService.ValidateCritic(critic, new(Cases()[0].Request.Snapshot, tied, [])));
        Assert.Contains("Competing playbooks", error.Message);
    }

    [Fact]
    public void UniqueRetrieval_DoesNotRecordTiedCandidates()
    {
        var artifacts = Run(Cases()[0]);
        Assert.Null(Events<DiagnosticsPlanEvent>(artifacts, "diagnostics.plan").Single().TiedCandidates);
        Assert.DoesNotContain("tiedCandidates", GoldenArtifactTestSupport.SerializeEventLogLines(artifacts.EventLogRecords));
        Assert.DoesNotContain("narrowingCandidates", GoldenArtifactTestSupport.SerializeEventLogLines(artifacts.EventLogRecords));
    }

    [Fact]
    public void NetworkRequiringProfile_IsDeniedBeforeTheRunnerStarts()
    {
        var runner = new SpyRunner();
        var snapshot = Snapshot();
        var tools = new RepositoryToolExecutor(snapshot,
            RepositoryDiagnosticsDefaults.Profiles(snapshot.Reference.ContentHash, Path.Combine(AppContext.BaseDirectory, "Aos.SandboxWorker.dll")), runner);
        var artifacts = Run(Cases().Single(c => c.CaseId == "RDI-11"), tools: tools);
        Assert.Equal("denied", artifacts.Outcome.Status);
        Assert.Equal("policy.denied", artifacts.Outcome.ReasonCode);
        Assert.Equal(0, runner.Calls);
        var denied = Assert.Single(Events<ToolExecutionEvent>(artifacts, "tool.execution"), t => t.Status == "denied");
        Assert.Equal("repo.network.denied", denied.Error);
        Assert.Equal("repo.test", denied.ToolId);
        Assert.Equal("stop", Events<DiagnosticsRecoveryDecision>(artifacts, "diagnostics.recovery").Single().Strategy);
    }

    [Fact]
    public async Task SuccessfulDiagnosis_ReplaysAfterItsSnapshotIsDeleted()
    {
        var staged = GoldenArtifactTestSupport.CreateTempDir("aos-diagnostics-staged-snapshot");
        var output = GoldenArtifactTestSupport.CreateTempDir("aos-diagnostics-snapshot-removed");
        try
        {
            foreach (var file in Directory.EnumerateFiles(SnapshotRoot, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(staged, Path.GetRelativePath(SnapshotRoot, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            var snapshot = RepositorySnapshot.Import(staged, RepositoryDiagnosticsDefaults.Revision, RepositoryDiagnosticsDefaults.CreatedAt);
            var tools = new RepositoryToolExecutor(snapshot,
                RepositoryDiagnosticsDefaults.Profiles(snapshot.Reference.ContentHash, Path.Combine(AppContext.BaseDirectory, "Aos.SandboxWorker.dll")),
                new PackagedRepositoryTestRunner());
            var scenario = Cases().Single(c => c.CaseId == "RDI-12");
            Assert.True(scenario.ReplayWithoutSnapshot);
            var artifacts = Run(scenario, tools: tools);
            Assert.Equal("succeeded", artifacts.Outcome.Status);
            Directory.Delete(staged, recursive: true);
            Assert.False(Directory.Exists(staged));
            await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), GoldenArtifactTestSupport.SerializeManifestRecord(artifacts.ManifestRecord));
            await File.WriteAllTextAsync(Path.Combine(output, "eventlog.jsonl"), GoldenArtifactTestSupport.SerializeEventLogLines(artifacts.EventLogRecords));
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exit = await ReplayCliRunner.RunAsync(["--workflow", "diagnostics", "--artifact-dir", output,
                "--hmac-key", GoldenArtifactTestSupport.GoldenHmacKey], stdout, stderr, CancellationToken.None);
            Assert.True(exit == 0, stderr.ToString());
        }
        finally
        {
            if (Directory.Exists(staged)) Directory.Delete(staged, true);
            Directory.Delete(output, true);
        }
    }

    [Fact]
    public void Recovery_WhenSecondAttemptHasNoEvidence_StopsAtDeterministicLimit()
    {
        var scenario = Cases().Single(c => c.CaseId == "RDI-07");
        var noMatch = new DeterministicPlannerService(new DeterministicPlaybookRetriever(new InMemoryPlaybookStore([])),
            new DeterministicPlaybookCandidateProvider(), new PlannerPlanValidator(RepositoryDiagnosticsDefaults.Catalog()));
        var artifacts = Run(scenario, planner: noMatch, tools: new SpyExecutor());
        Assert.Equal("failed", artifacts.Outcome.Status);
        Assert.Equal(2, artifacts.Outcome.Attempts);
        Assert.Equal("recovery.attempt_limit", artifacts.Outcome.ReasonCode);
        Assert.Equal(new[] { "alternate_retrieval", "stop" }, Events<DiagnosticsRecoveryDecision>(artifacts, "diagnostics.recovery").Select(e => e.Strategy));
    }

    [Fact]
    public void Critic_MissingEvidenceCannotBeApprovedByModel()
    {
        var client = new ModelClient("{\"decision\":\"accept\"}");
        var decisions = new RuleBasedDiagnosticsDecisions(client);
        var planning = new DiagnosticsPlanEvent(1, Cases()[0].Request.Task, null, null, null, "none", false, "planner.playbook.not_found");
        var decision = decisions.Assess(new(Snapshot().Reference, planning, []));
        Assert.Equal("evidence.none", decision.ReasonCode);
        Assert.Equal(0, client.Calls);
    }

    [Theory]
    [InlineData("{\"decision\":\"reject\"}", "model.rejected")]
    [InlineData("{\"decision\":\"inconclusive\"}", "model.inconclusive")]
    [InlineData("invalid", "model.invalid")]
    public void OptionalModelCritic_CanOnlyVetoGroundedEvidence(string json, string expected)
    {
        var artifacts = Run(Cases()[0], decisions: new RuleBasedDiagnosticsDecisions(new ModelClient(json)));
        Assert.Equal(expected, artifacts.Outcome.ReasonCode);
        Assert.Null(artifacts.Outcome.Diagnosis);
    }

    [Fact]
    public void AmbiguousEvidence_IsExplicitlyInconclusive()
    {
        var plan = new DiagnosticsPlanEvent(1, Cases()[0].Request.Task, new("0.1", "plan", "repository.diagnosis", []), null, null, "playbook", true, null);
        var decisions = new RuleBasedDiagnosticsDecisions();
        var critic = decisions.Assess(new(Snapshot().Reference, plan, []));
        Assert.Equal("inconclusive", critic.Decision);
        Assert.Null(decisions.Recover(critic, plan.Task, RepositoryDiagnosticsDefaults.Recovery()).NextTask);
    }

    [Fact]
    public void AttemptLimitAboveOne_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => Run(Cases()[0], retries: 2));
    }

    [Fact]
    public void RecoveryCannotChangeTaskIdentityOrUseAnUnconfiguredProfile()
    {
        var task = DiagnosticsWorkflowService.BindSnapshot(Cases()[0].Request.Task, Snapshot().Reference) with
            { Arguments = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["profileId"] = "pricing.broad" } };
        var critic = new DiagnosticsCriticDecision(1, "reject", "test.timeout", [], null);
        var decision = new RuleBasedDiagnosticsDecisions().Recover(critic, task, RepositoryDiagnosticsDefaults.Recovery());
        Assert.Throws<InvalidOperationException>(() => DiagnosticsWorkflowService.ValidateRecovery(
            decision with { NextTask = decision.NextTask! with { TaskId = "different-task" } }, critic, task, RepositoryDiagnosticsDefaults.Recovery()));
        Assert.Throws<InvalidOperationException>(() => DiagnosticsWorkflowService.ValidateRecovery(
            decision with { NextTask = task with { Arguments = new Dictionary<string, string> { ["profileId"] = "shell" } } }, critic, task, RepositoryDiagnosticsDefaults.Recovery()));
    }

    [Fact]
    public void CriticRejectsTruncatedAndMalformedToolEvidence()
    {
        var scenario = Cases()[0];
        var artifacts = Run(scenario);
        var planning = Events<DiagnosticsPlanEvent>(artifacts, "diagnostics.plan").Single();
        var tools = Events<ToolExecutionEvent>(artifacts, "tool.execution");
        var steps = tools.Select((t, index) => new PlannerStepExecutionResult(index + 1, $"step-{index + 1}", t.ToolId, Instant,
            new(t.InvocationId, new(t.ToolId, t.ToolVersion), t.Status, t.InputJson, t.OutputJson, t.Error, t.CapabilityDecision))).ToArray();
        var output = JsonSerializer.Deserialize<RepositoryToolOutput>(steps[1].ToolResult.OutputJson, RepositoryToolExecutor.JsonOptions)!;
        steps[1] = steps[1] with { ToolResult = steps[1].ToolResult with { OutputJson = JsonSerializer.Serialize(output with { Truncated = true }, RepositoryToolExecutor.JsonOptions) } };
        var critic = new RuleBasedDiagnosticsDecisions();
        Assert.Equal("evidence.truncated", critic.Assess(new(scenario.Request.Snapshot, planning, steps)).ReasonCode);
        steps[1] = steps[1] with { ToolResult = steps[1].ToolResult with { OutputJson = "invalid" } };
        Assert.Equal("tool.output.invalid", critic.Assess(new(scenario.Request.Snapshot, planning, steps)).ReasonCode);
    }

    [Theory]
    [InlineData("absent", "Diagnosis references absent")]
    [InlineData("wrong-line", "Cited evidence does not satisfy")]
    public async Task Replay_RejectsSignedInvalidEvidenceReference(string mutation, string expectedError)
    {
        var artifacts = Run(Cases()[0]);
        var entries = artifacts.EventLogRecords.Select(r => r.Entry).ToArray();
        var index = Array.FindIndex(entries, e => e.EventType == "diagnostics.critic");
        var critic = Events<DiagnosticsCriticDecision>(artifacts, "diagnostics.critic").Single();
        var evidence = critic.Evidence.ToArray();
        evidence[0] = mutation == "absent" ? evidence[0] with { InvocationId = "absent" } : evidence[0] with { Line = 1 };
        entries[index] = entries[index] with { Data = JsonSerializer.SerializeToElement(critic with
            { Evidence = evidence }, RepositoryToolExecutor.JsonOptions) };
        var chain = new HmacEventLogIntegrityChain(GoldenArtifactTestSupport.GoldenHmacKey, GoldenArtifactTestSupport.GoldenHmacKeyId);
        var records = chain.SignEntries(entries);
        var manifest = artifacts.ManifestRecord.Manifest with { EventLog = new("0.2", records.Count, records[^1].Integrity.ChainMac) };
        var dir = GoldenArtifactTestSupport.CreateTempDir("aos-diagnostics-signed-invalid");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), GoldenArtifactTestSupport.SerializeSignedManifest(manifest));
            await File.WriteAllTextAsync(Path.Combine(dir, "eventlog.jsonl"), GoldenArtifactTestSupport.SerializeEventLogLines(records));
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var result = await ReplayCliRunner.RunAsync(["--workflow", "diagnostics", "--artifact-dir", dir,
                "--hmac-key", GoldenArtifactTestSupport.GoldenHmacKey], stdout, stderr, CancellationToken.None);
            Assert.Equal(1, result);
            Assert.Contains(expectedError, stderr.ToString());
        }
        finally { Directory.Delete(dir, true); }
    }

    [ContainerFact]
    public async Task ContainerBackend_AllCasesMeetDiagnosticsGate()
    {
        var dir = GoldenArtifactTestSupport.CreateTempDir("aos-september-container-report");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var result = await DiagnosticsEvaluationRunner.RunAsync(["--snapshot-dir", SnapshotRoot, "--cases-root", CasesRoot,
            "--output-dir", dir, "--hmac-key", GoldenArtifactTestSupport.GoldenHmacKey, "--executor", "container-v1"], stdout, stderr, CancellationToken.None);
        Assert.True(result == 0, stdout + "\n" + stderr);
    }

    private static void UpdateGolden(string name, DiagnosticsWorkflowArtifacts artifacts)
    {
        if (Environment.GetEnvironmentVariable("AOS_UPDATE_GOLDEN") != "1") return;
        var dir = GoldenArtifactTestSupport.GetGoldenDir(name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), GoldenArtifactTestSupport.SerializeManifestRecord(artifacts.ManifestRecord) + "\n");
        File.WriteAllText(Path.Combine(dir, "eventlog.jsonl"), GoldenArtifactTestSupport.SerializeEventLogLines(artifacts.EventLogRecords));
        File.WriteAllText(Path.Combine(dir, "scenario.json"), JsonSerializer.Serialize(new
        { name, workflow = "diagnostics", manifestPath = "manifest.json", eventLogPath = "eventlog.jsonl" }, RepositoryToolExecutor.JsonOptions) + "\n");
    }

    private sealed class SpyExecutor : IToolExecutor
    {
        public int Calls { get; private set; }
        public ToolExecutionResult Execute(ToolExecutionRequest request) { Calls++; throw new InvalidOperationException("Unsafe tool invocation."); }
    }
    private sealed class SpyRunner : IRepositoryTestRunner
    {
        public int Calls { get; private set; }
        public RepositoryTestRun Run(RepositoryTestProfile profile) { Calls++; throw new InvalidOperationException("Runner must not start."); }
    }
    private sealed class ModelClient(string json) : ILocalModelCriticClient
    {
        public int Calls { get; private set; }
        public string AssessJson(DiagnosticsCriticInput input) { Calls++; return json; }
    }
}
