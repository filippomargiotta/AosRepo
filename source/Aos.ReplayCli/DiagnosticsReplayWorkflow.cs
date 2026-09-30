using System.Text.Json;
using Aos.WebApi.Models;
using Aos.WebApi.Services;

namespace Aos.ReplayCli;

internal sealed class DiagnosticsReplayWorkflow : IReplayWorkflow
{
    public string WorkflowName => DiagnosticsSchema.Workflow;

    public ReplayWorkflowArtifacts Replay(Manifest manifest, IReadOnlyList<EventLogRecord> records,
        IEventLogIntegrityChain chain, IManifestSigner signer)
    {
        var started = Read<DiagnosticsStartedEvent>(records, "diagnostics.started").Single();
        if (started.SchemaVersion != DiagnosticsSchema.Version || records[0].Entry.EventType != "diagnostics.started" ||
            records[^1].Entry.EventType != "diagnostics.completed") throw new InvalidOperationException("Invalid diagnostics event boundary/schema.");
        var plans = Read<DiagnosticsPlanEvent>(records, "diagnostics.plan").ToArray();
        var policy = plans.Where(p => p.Policy is not null).Select(p => p.Policy!).DistinctBy(p => (p.PlaybookId, p.Version)).ToArray();
        var recordedPlanner = new RecordedPlanner(plans);
        var decisions = new RecordedDecisions(Read<DiagnosticsCriticDecision>(records, "diagnostics.critic"),
            Read<DiagnosticsRecoveryDecision>(records, "diagnostics.recovery"));
        var tools = new RecordedTools(Read<ToolExecutionEvent>(records, "tool.execution"));
        var catalog = new AllowedActionCatalog(started.AllowedActions);
        // A signed historic catalogue is evidence, not permission to add arbitrary actions during replay.
        if (catalog.Actions.Any(a => a.ActionId is not ("repo.search" or "repo.read" or "repo.test") ||
            a.Tool.ToolId != a.ActionId || a.Tool.Version != "1.0")) throw new InvalidOperationException("Unsafe diagnostics catalogue.");
        var clock = new ReplayTimeSource([manifest.StartedAtUtc, .. records.Select(r => r.Entry.OccurredAtUtc)], manifest.TimeSource);
        var service = new DiagnosticsWorkflowService(recordedPlanner, policy, catalog,
            new PlannerStepExecutor(catalog, tools, new CapabilityEnforcingToolExecutor(tools, tools), clock),
            decisions, started.Recovery, new RecordedSeed(manifest.Seed), clock,
            new RecordedRouter(manifest.RoutingDecisions.Single()), chain, signer);
        var actual = service.CreateArtifacts(manifest.RunId, started.Request);
        if (!recordedPlanner.Complete || !decisions.Complete || !tools.Complete)
            throw new InvalidOperationException("Unconsumed diagnostics events.");
        return new(actual.ManifestRecord, actual.EventLogRecords);
    }

    private static IEnumerable<T> Read<T>(IReadOnlyList<EventLogRecord> records, string type) =>
        records.Where(r => r.Entry.EventType == type).Select(r => JsonSerializer.SerializeToElement(r.Entry.Data, RepositoryToolExecutor.JsonOptions)
            .Deserialize<T>(RepositoryToolExecutor.JsonOptions) ?? throw new InvalidOperationException($"Invalid {type} payload."));

    private sealed class RecordedPlanner(IEnumerable<DiagnosticsPlanEvent> events) : IPlannerService
    {
        private readonly Queue<DiagnosticsPlanEvent> _events = new(events);
        public bool Complete => _events.Count == 0;
        public PlannerPlanningResult Plan(PlannerTaskRequest task)
        {
            if (!_events.TryDequeue(out var recorded) ||
                JsonSerializer.Serialize(task, RepositoryToolExecutor.JsonOptions) != JsonSerializer.Serialize(recorded.Task, RepositoryToolExecutor.JsonOptions))
                throw new InvalidOperationException("Recorded diagnostics task differs from replay task.");
            PlaybookMatch? selected = recorded.Selection is null ? null : new PlaybookMatch(
                new PlannerPlaybook("0.1", recorded.Selection.PlaybookId, recorded.Selection.Version, task.TaskClass,
                    "recorded", [], []), recorded.Selection.Score, recorded.Selection.MatchedTerms);
            return new(recorded.IsValid ? "validated" : "rejected", recorded.Task, selected, recorded.CandidateSource,
                recorded.Plan, new(recorded.IsValid, []), recorded.ErrorCode, null);
        }
    }

    private sealed class RecordedDecisions(IEnumerable<DiagnosticsCriticDecision> critics,
        IEnumerable<DiagnosticsRecoveryDecision> recoveries) : IDiagnosticsDecisions
    {
        private readonly Queue<DiagnosticsCriticDecision> _critics = new(critics);
        private readonly Queue<DiagnosticsRecoveryDecision> _recoveries = new(recoveries);
        public bool Complete => _critics.Count == 0 && _recoveries.Count == 0;
        public DiagnosticsCriticDecision Assess(DiagnosticsCriticInput input) =>
            _critics.TryDequeue(out var value) ? value : throw new InvalidOperationException("Missing recorded critic decision.");
        public DiagnosticsRecoveryDecision Recover(DiagnosticsCriticDecision critic, PlannerTaskRequest task, DiagnosticsRecoverySettings settings) =>
            _recoveries.TryDequeue(out var value) ? value : throw new InvalidOperationException("Missing recorded recovery decision.");
    }

    private sealed class RecordedTools : IToolExecutor, ICapabilityTokenIssuer, ICapabilityTokenValidator
    {
        private readonly Queue<ToolExecutionEvent> _allowed;
        private readonly IReadOnlyDictionary<string, ToolExecutionEvent> _byId;
        public bool Complete => _allowed.Count == 0;
        public RecordedTools(IEnumerable<ToolExecutionEvent> tools)
        {
            var list = tools.ToArray();
            _allowed = new(list.Where(t => t.CapabilityDecision.Decision == "allow"));
            _byId = list.ToDictionary(t => t.InvocationId, StringComparer.Ordinal);
        }
        public string Issue(ToolCapabilityScope scope, DateTimeOffset issuedAtUtc) => $"recorded:{scope.InvocationId}";
        public CapabilityDecision Validate(string token, ToolCapabilityScope scope, DateTimeOffset validationTimeUtc)
        {
            if (!_byId.TryGetValue(scope.InvocationId, out var item) || item.ToolId != scope.ToolId || item.ToolVersion != scope.ToolVersion ||
                scope.Action != item.ToolId) throw new InvalidOperationException("Recorded capability scope differs.");
            return item.CapabilityDecision;
        }
        public ToolExecutionResult Execute(ToolExecutionRequest request)
        {
            if (!_allowed.TryDequeue(out var item) || item.InvocationId != request.InvocationId || item.ToolId != request.Tool.ToolId ||
                item.ToolVersion != request.Tool.Version || item.InputJson != request.InputJson)
                throw new InvalidOperationException("Recorded tool invocation differs.");
            return new(item.InvocationId, request.Tool, item.Status, item.InputJson, item.OutputJson, item.Error);
        }
    }

    private sealed class RecordedSeed(SeedInfo seed) : ISeedProvider { public SeedInfo GetLockedSeed(string runId) => seed; }
    private sealed class RecordedRouter(RouterSelectionResult routing) : IRouterService
    {
        public RouterSelectionResult SelectModel(RouterSelectionRequest request) => request.TaskClass == routing.TaskClass
            ? routing : throw new InvalidOperationException("Recorded route task class differs.");
    }
}
