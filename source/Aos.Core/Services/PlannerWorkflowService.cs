using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Microsoft.Extensions.Options;

namespace Aos.WebApi.Services;

public sealed class PlannerWorkflowService : IPlannerWorkflowService
{
    private readonly ISeedProvider _seedProvider;
    private readonly ITimeSource _timeSource;
    private readonly PlannerWorkflowOptions _options;
    private readonly IRouterService _routerService;
    private readonly IPlannerService _plannerService;
    private readonly PlannerStepExecutor _stepExecutor;
    private readonly AllowedActionCatalog _actionCatalog;
    private readonly IEventLogIntegrityChain _eventLogIntegrityChain;
    private readonly IManifestSigner _manifestSigner;

    public PlannerWorkflowService(
        ISeedProvider seedProvider,
        ITimeSource timeSource,
        IOptions<PlannerWorkflowOptions> options,
        IRouterService routerService,
        IPlannerService plannerService,
        PlannerStepExecutor stepExecutor,
        AllowedActionCatalog actionCatalog,
        IEventLogIntegrityChain eventLogIntegrityChain,
        IManifestSigner manifestSigner)
    {
        _seedProvider = seedProvider ?? throw new ArgumentNullException(nameof(seedProvider));
        _timeSource = timeSource ?? throw new ArgumentNullException(nameof(timeSource));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _routerService = routerService ?? throw new ArgumentNullException(nameof(routerService));
        _plannerService = plannerService ?? throw new ArgumentNullException(nameof(plannerService));
        _stepExecutor = stepExecutor ?? throw new ArgumentNullException(nameof(stepExecutor));
        _actionCatalog = actionCatalog ?? throw new ArgumentNullException(nameof(actionCatalog));
        _eventLogIntegrityChain = eventLogIntegrityChain ?? throw new ArgumentNullException(nameof(eventLogIntegrityChain));
        _manifestSigner = manifestSigner ?? throw new ArgumentNullException(nameof(manifestSigner));
    }

    public PlannerWorkflowArtifacts CreateArtifacts(
        string runId,
        PlannerTaskRequest task,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("Run id is required.", nameof(runId));
        }

        ArgumentNullException.ThrowIfNull(task);
        var startedAt = _timeSource.NowUtc();
        var seed = _seedProvider.GetLockedSeed(runId);
        var routing = ResolveRoutingDecision(task.TaskClass);
        var planning = _plannerService.Plan(task);
        if (!string.Equals(planning.Status, "validated", StringComparison.Ordinal) ||
            planning.Plan is null || planning.SelectedPlaybook is null)
        {
            throw new InvalidOperationException(
                $"Planner rejected task '{task.TaskId}': {planning.ErrorCode}: {planning.Error}");
        }

        var planEventTime = _timeSource.NowUtc();
        var execution = _stepExecutor.Execute(runId, planning.Plan, cancellationToken);
        var completedAt = _timeSource.NowUtc();
        var entries = BuildEventEntries(runId, planning, execution, planEventTime, completedAt);
        var records = _eventLogIntegrityChain.SignEntries(entries);
        var capabilityPolicies = execution.Steps.Select(step =>
        {
            var decision = step.ToolResult.CapabilityDecision ?? throw new InvalidOperationException(
                $"Planner step '{step.StepId}' result must include a capability decision.");
            return new PolicyDecision(decision.PolicyId, decision.Decision, decision.ReasonCode);
        });
        var manifest = new Manifest(
            SchemaVersions.CurrentManifestVersion,
            runId,
            seed,
            _timeSource.Describe(),
            [MapModelRef(routing.SelectedCandidate!)],
            ResolveUsedTools(planning.Plan),
            ResolveConfiguredPolicies().Concat(capabilityPolicies).ToArray(),
            [routing],
            new EventLogSummary(records[0].SchemaVersion, records.Count, records[^1].Integrity.ChainMac),
            startedAt,
            completedAt);

        var errors = ManifestValidator.Validate(manifest);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", errors));
        }

        return new PlannerWorkflowArtifacts(
            _manifestSigner.SignManifest(manifest),
            records,
            planning,
            execution);
    }

    private IReadOnlyList<EventLogEntry> BuildEventEntries(
        string runId,
        PlannerPlanningResult planning,
        PlannerExecutionResult execution,
        DateTimeOffset planEventTime,
        DateTimeOffset completedAt)
    {
        var selected = planning.SelectedPlaybook!;
        var entries = new List<EventLogEntry>
        {
            new(
                runId,
                "planner.plan",
                new PlannerPlanEvent(
                    SchemaVersions.CurrentManifestVersion,
                    planning.Task,
                    planning.CandidateSource,
                    new PlannerPlaybookSelection(
                        selected.Playbook.PlaybookId,
                        selected.Playbook.Version,
                        selected.Score,
                        selected.MatchedTerms),
                    _actionCatalog.Actions,
                    planning.Plan!,
                    planning.Validation),
                planEventTime)
        };

        entries.AddRange(execution.Steps.Select(step => new EventLogEntry(
            runId,
            "tool.execution",
            MapToolExecutionEvent(step.ToolResult),
            step.RequestedAtUtc)));
        entries.Add(new EventLogEntry(
            runId,
            "workflow.planner",
            new PlannerWorkflowEvent(
                SchemaVersions.CurrentManifestVersion,
                planning.Task.TaskId,
                planning.Plan!.PlanId,
                execution.Status,
                execution.Steps.Count,
                execution.ErrorCode),
            completedAt));
        return entries;
    }

    private RouterSelectionResult ResolveRoutingDecision(string taskClass)
    {
        var result = _routerService.SelectModel(new RouterSelectionRequest(
            taskClass,
            _options.Routing.MaxLatencyMs,
            _options.Routing.MaxCostPer1KTokens,
            _options.Routing.MinQualityScore,
            _options.Routing.RequiredComplianceTags));
        if (result.SelectedCandidate is null)
        {
            throw new InvalidOperationException($"Router did not select a model for planner task class '{taskClass}'.");
        }

        return result;
    }

    private IReadOnlyList<ToolRef> ResolveUsedTools(PlannerPlan plan)
    {
        var seen = new HashSet<(string ToolId, string Version)>();
        var tools = new List<ToolRef>();
        foreach (var step in plan.Steps)
        {
            if (!_actionCatalog.TryGet(step.ActionId, out var action))
            {
                throw new InvalidOperationException($"Plan action '{step.ActionId}' is missing from the action catalogue.");
            }

            if (seen.Add((action.Tool.ToolId, action.Tool.Version)))
            {
                tools.Add(action.Tool);
            }
        }

        return tools.AsReadOnly();
    }

    private IReadOnlyList<PolicyDecision> ResolveConfiguredPolicies() =>
        _options.PolicyDecisions.Select(policy => new PolicyDecision(
            Require(policy.PolicyId, "PlannerWorkflow.PolicyDecisions[].PolicyId"),
            Require(policy.Decision, "PlannerWorkflow.PolicyDecisions[].Decision"),
            policy.Reason)).ToArray();

    private static ToolExecutionEvent MapToolExecutionEvent(ToolExecutionResult result) =>
        new(
            result.InvocationId,
            result.Tool.ToolId,
            result.Tool.Version,
            result.Status,
            result.InputJson,
            result.OutputJson,
            result.Error,
            result.CapabilityDecision ?? throw new InvalidOperationException(
                "Tool execution result must include a capability decision."));

    private static ModelRef MapModelRef(RouterModelCandidate candidate) =>
        new(candidate.ModelId, candidate.Provider, candidate.Version);

    private static string Require(string value, string path) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{path} is required.") : value;
}
