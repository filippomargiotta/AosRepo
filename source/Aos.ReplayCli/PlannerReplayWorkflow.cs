using System.Text.Json;
using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Aos.WebApi.Services;

namespace Aos.ReplayCli;

internal sealed class PlannerReplayWorkflow : IReplayWorkflow
{
    public string WorkflowName => "planner";

    public ReplayWorkflowArtifacts Replay(
        Manifest manifest,
        IReadOnlyList<EventLogRecord> expectedRecords,
        IEventLogIntegrityChain eventLogIntegrityChain,
        IManifestSigner manifestSigner)
    {
        var planEvent = ReadSingleEvent<PlannerPlanEvent>(expectedRecords, "planner.plan");
        var recordedTools = ReadEvents<ToolExecutionEvent>(expectedRecords, "tool.execution");
        var timeSource = new ReplayTimeSource(
            [manifest.StartedAtUtc, .. expectedRecords.Select(record => record.Entry.OccurredAtUtc)],
            manifest.TimeSource);
        var catalog = new AllowedActionCatalog(planEvent.AllowedActions);
        var recordedPlanValidation = new PlannerPlanValidator(catalog).Validate(planEvent.Plan);
        if (!recordedPlanValidation.IsValid || !planEvent.Validation.IsValid ||
            !string.Equals(planEvent.Task.TaskClass, planEvent.Plan.TaskClass, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Recorded planner.plan evidence does not contain a valid plan for its task.");
        }
        var capabilityService = new RecordedCapabilityService(recordedTools);
        var stepExecutor = new PlannerStepExecutor(
            catalog,
            capabilityService,
            new CapabilityEnforcingToolExecutor(
                capabilityService,
                new RecordedToolExecutor(recordedTools)),
            timeSource);
        var service = new PlannerWorkflowService(
            new FixedSeedProvider(manifest.Seed),
            timeSource,
            Microsoft.Extensions.Options.Options.Create(CreateOptions(manifest)),
            new FixedRouterService(manifest.RoutingDecisions.Single()),
            new RecordedPlannerService(planEvent),
            stepExecutor,
            catalog,
            eventLogIntegrityChain,
            manifestSigner);

        var artifacts = service.CreateArtifacts(manifest.RunId, planEvent.Task);
        return new ReplayWorkflowArtifacts(artifacts.ManifestRecord, artifacts.EventLogRecords);
    }

    private static PlannerWorkflowOptions CreateOptions(Manifest manifest)
    {
        var routing = manifest.RoutingDecisions.Single().Policy.EffectiveConstraints;
        return new PlannerWorkflowOptions
        {
            Routing = new PlannerRoutingOptions
            {
                MaxLatencyMs = routing.MaxLatencyMs,
                MaxCostPer1KTokens = routing.MaxCostPer1KTokens,
                MinQualityScore = routing.MinQualityScore,
                RequiredComplianceTags = routing.RequiredComplianceTags.ToList()
            },
            PolicyDecisions = manifest.PolicyDecisions
                .Where(policy => !string.Equals(
                    policy.PolicyId,
                    HmacJwtCapabilityTokenService.PolicyId,
                    StringComparison.Ordinal))
                .Select(policy => new HelloWorkflowPolicyOptions
                {
                    PolicyId = policy.PolicyId,
                    Decision = policy.Decision,
                    Reason = policy.Reason
                })
                .ToList()
        };
    }

    private static T ReadSingleEvent<T>(IReadOnlyList<EventLogRecord> records, string eventType)
    {
        var matching = records.Where(record => string.Equals(
            record.Entry.EventType,
            eventType,
            StringComparison.Ordinal)).ToArray();
        if (matching.Length != 1)
        {
            throw new InvalidOperationException($"Planner replay requires exactly one {eventType} event.");
        }

        return Deserialize<T>(matching[0].Entry.Data, eventType);
    }

    private static IReadOnlyList<T> ReadEvents<T>(IReadOnlyList<EventLogRecord> records, string eventType) =>
        records
            .Where(record => string.Equals(record.Entry.EventType, eventType, StringComparison.Ordinal))
            .Select(record => Deserialize<T>(record.Entry.Data, eventType))
            .ToArray();

    private static T Deserialize<T>(object? value, string eventType) =>
        JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidOperationException($"Recorded {eventType} payload is invalid.");

    private sealed class FixedSeedProvider : ISeedProvider
    {
        private readonly SeedInfo _seed;
        public FixedSeedProvider(SeedInfo seed) => _seed = seed;
        public SeedInfo GetLockedSeed(string runId) => _seed;
    }

    private sealed class FixedRouterService : IRouterService
    {
        private readonly RouterSelectionResult _recorded;
        public FixedRouterService(RouterSelectionResult recorded) => _recorded = recorded;

        public RouterSelectionResult SelectModel(RouterSelectionRequest request)
        {
            if (!string.Equals(request.TaskClass, _recorded.TaskClass, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Planner replay requested task class '{request.TaskClass}', expected '{_recorded.TaskClass}'.");
            }

            return _recorded;
        }
    }

    private sealed class RecordedPlannerService : IPlannerService
    {
        private readonly PlannerPlanEvent _recorded;
        public RecordedPlannerService(PlannerPlanEvent recorded) => _recorded = recorded;

        public PlannerPlanningResult Plan(PlannerTaskRequest task)
        {
            if (!Equals(task, _recorded.Task))
            {
                throw new InvalidOperationException("Planner replay task does not match the recorded planner.plan task.");
            }

            var selection = _recorded.SelectedPlaybook;
            var placeholder = new PlannerPlaybook(
                PlannerSchemaVersions.CurrentPlaybookVersion,
                selection.PlaybookId,
                selection.Version,
                task.TaskClass,
                "recorded planner replay selection",
                [],
                [new PlannerPlaybookStep(_recorded.Plan.Steps[0].ActionId, new Dictionary<string, string>())]);
            return new PlannerPlanningResult(
                "validated",
                task,
                new PlaybookMatch(placeholder, selection.Score, selection.MatchedTerms),
                _recorded.CandidateSource,
                _recorded.Plan,
                _recorded.Validation,
                null,
                null);
        }
    }

    private sealed class RecordedCapabilityService : ICapabilityTokenIssuer, ICapabilityTokenValidator
    {
        private readonly IReadOnlyDictionary<string, ToolExecutionEvent> _events;

        public RecordedCapabilityService(IEnumerable<ToolExecutionEvent> events)
        {
            _events = events.ToDictionary(item => item.InvocationId, StringComparer.Ordinal);
        }

        public string Issue(ToolCapabilityScope scope, DateTimeOffset issuedAtUtc) =>
            $"recorded:{scope.InvocationId}";

        public CapabilityDecision Validate(
            string capabilityToken,
            ToolCapabilityScope expectedScope,
            DateTimeOffset validationTimeUtc)
        {
            if (!_events.TryGetValue(expectedScope.InvocationId, out var recorded) ||
                !string.Equals(recorded.ToolId, expectedScope.ToolId, StringComparison.Ordinal) ||
                !string.Equals(recorded.ToolVersion, expectedScope.ToolVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Replay capability scope does not match recorded tool evidence.");
            }

            return recorded.CapabilityDecision;
        }
    }

    private sealed class RecordedToolExecutor : IToolExecutor
    {
        private readonly Queue<ToolExecutionEvent> _allowedEvents;

        public RecordedToolExecutor(IEnumerable<ToolExecutionEvent> events)
        {
            _allowedEvents = new Queue<ToolExecutionEvent>(events.Where(item => string.Equals(
                item.CapabilityDecision.Decision,
                "allow",
                StringComparison.Ordinal)));
        }

        public ToolExecutionResult Execute(ToolExecutionRequest request)
        {
            if (!_allowedEvents.TryDequeue(out var recorded) ||
                !string.Equals(request.InvocationId, recorded.InvocationId, StringComparison.Ordinal) ||
                !string.Equals(request.Tool.ToolId, recorded.ToolId, StringComparison.Ordinal) ||
                !string.Equals(request.Tool.Version, recorded.ToolVersion, StringComparison.Ordinal) ||
                !string.Equals(request.InputJson, recorded.InputJson, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Planner replay tool request does not match recorded tool evidence.");
            }

            return new ToolExecutionResult(
                recorded.InvocationId,
                request.Tool,
                recorded.Status,
                recorded.InputJson,
                recorded.OutputJson,
                recorded.Error);
        }
    }
}
