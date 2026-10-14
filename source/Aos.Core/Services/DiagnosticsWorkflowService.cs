using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public sealed class DiagnosticsWorkflowService(
    IPlannerService planner, IReadOnlyList<DiagnosticPlaybookPolicy> policies, AllowedActionCatalog catalog,
    PlannerStepExecutor executor, IDiagnosticsDecisions decisions, DiagnosticsRecoverySettings recovery,
    ISeedProvider seeds, ITimeSource time, IRouterService router,
    IEventLogIntegrityChain chain, IManifestSigner signer)
{
    public DiagnosticsWorkflowArtifacts CreateArtifacts(string runId, DiagnosticsRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId)) throw new ArgumentException("Run id is required.");
        if (request?.Snapshot is null || request.Task?.Arguments is null || request.Task.Terms is null ||
            string.IsNullOrWhiteSpace(request.Task.TaskId) || string.IsNullOrWhiteSpace(request.Task.TaskClass))
            throw new ArgumentException("A snapshot and a complete diagnostic task are required.");
        if (recovery.MaxRetries is < 0 or > 1) throw new ArgumentException("September permits zero or one retry.");
        if (request.Snapshot.SchemaVersion != DiagnosticsSchema.Version ||
            request.Snapshot.ContentHash?.Length != 64 || request.Snapshot.ContentHash.Any(c => !Uri.IsHexDigit(c)) ||
            request.Snapshot.SnapshotId != $"sha256:{request.Snapshot.ContentHash}" || string.IsNullOrWhiteSpace(request.Snapshot.SourceRevision))
            throw new ArgumentException("Invalid snapshot reference.");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var toolMs = 0d;
        var executionMs = 0d;
        var started = time.NowUtc();
        var seed = seeds.GetLockedSeed(runId);
        var routing = router.SelectModel(new RouterSelectionRequest(request.Task.TaskClass, null, null, null, []));
        if (routing.SelectedCandidate is null) throw new InvalidOperationException("No diagnostics routing candidate.");
        var entries = new List<EventLogEntry>();
        // Freeze domain JSON before signing: nested dates must use the same encoding after disk deserialization.
        void Add(string type, object payload) => entries.Add(new(runId, type,
            System.Text.Json.JsonSerializer.SerializeToElement(payload, RepositoryToolExecutor.JsonOptions), time.NowUtc()));
        var task = BindSnapshot(request.Task, request.Snapshot);
        request = request with { Task = task };
        Add("diagnostics.started", new DiagnosticsStartedEvent(DiagnosticsSchema.Version, SchemaVersions.CurrentManifestVersion, request, recovery, catalog.Actions));
        var authorizedTools = catalog.Actions.Select(a => a.Tool).Distinct().ToArray();
        var capabilityPolicies = new List<PolicyDecision>();
        DiagnosticsOutcome outcome;
        DiagnosticsPlanEvent planEvent;
        for (var attempt = 1; ; attempt++)
        {
            var planning = planner.Plan(task);
            var valid = planning.Status == "validated" && planning.Plan is not null && planning.SelectedPlaybook is not null &&
                new PlannerPlanValidator(catalog).Validate(planning.Plan).IsValid;
            var selected = planning.SelectedPlaybook;
            var policy = selected is null ? null : policies.SingleOrDefault(p => p.PlaybookId == selected.Playbook.PlaybookId && p.Version == selected.Playbook.Version);
            // Equal top-score playbooks are competing causes: record them and execute neither rather than tie-break lexically.
            var tied = planning.TiedCandidates?.Select(Selection).ToArray();
            planEvent = new DiagnosticsPlanEvent(attempt, planning.Task, valid ? planning.Plan : null,
                selected is null ? null : Selection(selected),
                policy, planning.CandidateSource, valid, valid ? null : planning.ErrorCode ?? "planner.plan.invalid", tied);
            Add("diagnostics.plan", planEvent);
            IReadOnlyList<PlannerStepExecutionResult> steps = [];
            if (valid && tied is null)
            {
                var executionTimer = System.Diagnostics.Stopwatch.StartNew();
                var result = executor.Execute(runId, planning.Plan!, cancellationToken, attempt);
                executionMs += executionTimer.Elapsed.TotalMilliseconds;
                steps = result.Steps;
                foreach (var step in steps)
                {
                    var tool = step.ToolResult;
                    var capability = tool.CapabilityDecision ?? throw new InvalidOperationException("Missing capability decision.");
                    capabilityPolicies.Add(new(capability.PolicyId, capability.Decision, capability.ReasonCode));
                    entries.Add(new(runId, "tool.execution", new ToolExecutionEvent(tool.InvocationId, tool.Tool.ToolId, tool.Tool.Version,
                        tool.Status, tool.InputJson, tool.OutputJson, tool.Error, capability), step.RequestedAtUtc));
                    toolMs += tool.SandboxExecution?.ExecutionLatencyMs ?? 0;
                }
            }
            var critic = cancellationToken.IsCancellationRequested
                ? new DiagnosticsCriticDecision(attempt, "reject", "execution.cancelled", [], null)
                : decisions.Assess(new(request.Snapshot, planEvent, steps));
            ValidateCritic(critic, new(request.Snapshot, planEvent, steps));
            Add("diagnostics.critic", critic);
            var next = decisions.Recover(critic, task, recovery);
            ValidateRecovery(next, critic, task, recovery);
            Add("diagnostics.recovery", next);
            if (next.NextTask is not null)
            {
                task = BindSnapshot(next.NextTask, request.Snapshot);
                continue;
            }
            outcome = new DiagnosticsOutcome(critic.Decision == "accept" ? "succeeded" :
                critic.Decision == "inconclusive" ? "inconclusive" :
                critic.ReasonCode is "policy.denied" or "plan.invalid" or "snapshot.mismatch" ? "denied" : "failed",
                next.TerminalReason == "attempt_limit" ? "recovery.attempt_limit" : critic.ReasonCode,
                attempt, critic.Diagnosis, critic.Evidence,
                critic.ReasonCode == "evidence.ambiguous" ? planEvent.TiedCandidates?.Select(c => c.PlaybookId).ToArray() : null);
            break;
        }
        Add("diagnostics.completed", outcome);
        var records = chain.SignEntries(entries);
        var candidate = routing.SelectedCandidate;
        var manifest = new Manifest(SchemaVersions.CurrentManifestVersion, runId, seed, time.Describe(),
            [new ModelRef(candidate.ModelId, candidate.Provider, candidate.Version)], authorizedTools,
            [new("repository-read-only", "allow", "snapshot-bound named actions only"), .. capabilityPolicies],
            [routing], new(records[0].SchemaVersion, records.Count, records[^1].Integrity.ChainMac), started, entries[^1].OccurredAtUtc);
        var errors = ManifestValidator.Validate(manifest);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
        return new(signer.SignManifest(manifest), records, outcome,
            new(Math.Max(0, timer.Elapsed.TotalMilliseconds - executionMs), toolMs, Math.Max(0, executionMs - toolMs)));
    }

    private static PlannerPlaybookSelection Selection(PlaybookMatch match) =>
        new(match.Playbook.PlaybookId, match.Playbook.Version, match.Score, match.MatchedTerms);

    public static PlannerTaskRequest BindSnapshot(PlannerTaskRequest task, RepositorySnapshotRef snapshot)
    {
        var args = new SortedDictionary<string, string>(task.Arguments.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal)
            { ["snapshotId"] = snapshot.SnapshotId, ["snapshotHash"] = snapshot.ContentHash };
        return task with { Arguments = args, Terms = task.Terms.Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() };
    }

    // Validate recorded/model decisions against captured evidence without repeating a critic assessment.
    public static void ValidateCritic(DiagnosticsCriticDecision critic, DiagnosticsCriticInput input)
    {
        if (critic.Attempt != input.Planning.Attempt || critic.Decision is not ("accept" or "reject" or "inconclusive") ||
            string.IsNullOrWhiteSpace(critic.ReasonCode)) throw new InvalidOperationException("Invalid critic decision.");
        if (input.Planning.TiedCandidates is { Count: > 1 } && (critic.Decision == "accept" || input.Steps.Count != 0))
            throw new InvalidOperationException("Competing playbooks cannot produce an executed or accepted diagnosis.");
        if (critic.Decision != "accept")
        {
            if (critic.Diagnosis is not null || critic.Evidence.Count != 0) throw new InvalidOperationException("Rejected evidence cannot support a diagnosis.");
            return;
        }
        if (!input.Planning.IsValid || string.IsNullOrWhiteSpace(critic.Diagnosis) || critic.Evidence.Count == 0)
            throw new InvalidOperationException("Accepted diagnosis needs a validated plan and evidence.");
        if (input.Planning.Policy is null || critic.Diagnosis != input.Planning.Policy.Diagnosis)
            throw new InvalidOperationException("Diagnosis differs from its recorded grounding policy.");
        var cited = new List<(DiagnosticEvidenceRef Reference, RepositoryToolOutput Output)>();
        foreach (var evidence in critic.Evidence)
        {
            var step = input.Steps.SingleOrDefault(s => s.ToolResult.InvocationId == evidence.InvocationId);
            if (step is null || step.ToolResult.Status != "succeeded" || evidence.ContentHash != input.Snapshot.ContentHash)
                throw new InvalidOperationException("Diagnosis references absent or unsuccessful evidence.");
            var output = System.Text.Json.JsonSerializer.Deserialize<RepositoryToolOutput>(step.ToolResult.OutputJson, RepositoryToolExecutor.JsonOptions)!;
            if (output.Snapshot != input.Snapshot || output.Truncated || output.Kind != evidence.Kind ||
                (evidence.Kind == "read" && !output.Lines.Any(l => l.Path == evidence.Path && l.Line == evidence.Line)) ||
                (evidence.Kind == "test" && (output.ExitCode is null || evidence.Path is not null || evidence.Line is not null)) ||
                evidence.Kind is not ("read" or "test"))
                throw new InvalidOperationException("Diagnosis evidence binding is invalid.");
            cited.Add((evidence, output));
        }
        foreach (var requirement in input.Planning.Policy.Requirements)
            if (!cited.Any(item => requirement.Kind == "read" ?
                item.Reference.Kind == "read" && item.Reference.Path == requirement.Path && item.Output.Lines.Any(line =>
                    line.Path == requirement.Path && line.Line == item.Reference.Line && line.Text.Contains(requirement.Contains, StringComparison.Ordinal)) :
                requirement.Kind == "test" && item.Reference.Kind == "test" && item.Output.FailureClassification == "test_failed" &&
                    item.Output.Stdout?.Contains(requirement.Contains, StringComparison.Ordinal) == true))
                throw new InvalidOperationException("Cited evidence does not satisfy the recorded grounding contract.");
    }

    public static void ValidateRecovery(DiagnosticsRecoveryDecision next, DiagnosticsCriticDecision critic,
        PlannerTaskRequest task, DiagnosticsRecoverySettings settings)
    {
        if (next.Attempt != critic.Attempt || next.PriorFailure != critic.ReasonCode)
            throw new InvalidOperationException("Invalid recovery transition.");
        if (next.NextTask is null)
        {
            if (next.Strategy != "stop" || next.TerminalReason is not ("succeeded" or "failed" or "denied" or "inconclusive" or "attempt_limit"))
                throw new InvalidOperationException("Invalid recovery terminal state.");
            if ((critic.Decision == "accept" && next.TerminalReason != "succeeded") ||
                (critic.Decision == "inconclusive" && next.TerminalReason != "inconclusive") ||
                (next.TerminalReason == "attempt_limit" && critic.Attempt <= settings.MaxRetries))
                throw new InvalidOperationException("Recovery terminal reason differs from critic/attempt limit.");
            return;
        }
        if (critic.Attempt > settings.MaxRetries || critic.Decision != "reject" || next.TerminalReason is not null)
            throw new InvalidOperationException("Invalid recovery attempt/authority.");
        PlannerTaskRequest expected;
        if (next.Strategy == "narrower_test" && critic.ReasonCode == "test.timeout" &&
            task.Arguments.TryGetValue("profileId", out var prior) && settings.NarrowerProfiles.TryGetValue(prior, out var narrower) && prior != narrower)
        {
            var args = new SortedDictionary<string, string>(task.Arguments.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal)
                { ["profileId"] = narrower };
            expected = task with { Arguments = args };
        }
        else if (next.Strategy == "alternate_retrieval" && critic.ReasonCode == "evidence.none" && settings.AlternateTerms.Count > 0)
            expected = task with { Terms = settings.AlternateTerms };
        else throw new InvalidOperationException("Invalid recovery strategy.");
        if (System.Text.Json.JsonSerializer.Serialize(expected, RepositoryToolExecutor.JsonOptions) !=
            System.Text.Json.JsonSerializer.Serialize(next.NextTask, RepositoryToolExecutor.JsonOptions))
            throw new InvalidOperationException("Recovery task differs from the configured bounded strategy.");
    }
}
