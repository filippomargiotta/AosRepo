using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Microsoft.Extensions.Options;

namespace Aos.WebApi.Services;

public sealed class HelloWorkflowService : IHelloWorkflowService
{
    private readonly ISeedProvider _seedProvider;
    private readonly ITimeSource _timeSource;
    private readonly HelloWorkflowOptions _options;
    private readonly IRouterService _routerService;
    private readonly IEventLogIntegrityChain _eventLogIntegrityChain;
    private readonly IManifestSigner _manifestSigner;

    public HelloWorkflowService(
        ISeedProvider seedProvider,
        ITimeSource timeSource,
        IOptions<HelloWorkflowOptions> options,
        IRouterService routerService,
        IEventLogIntegrityChain eventLogIntegrityChain,
        IManifestSigner manifestSigner)
    {
        _seedProvider = seedProvider;
        _timeSource = timeSource;
        _options = options.Value;
        _routerService = routerService;
        _eventLogIntegrityChain = eventLogIntegrityChain;
        _manifestSigner = manifestSigner;
    }

    public HelloWorkflowArtifacts CreateHelloArtifacts(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("Run id is required.", nameof(runId));
        }

        var now = _timeSource.NowUtc();
        var seed = _seedProvider.GetLockedSeed(runId);
        var timeSourceInfo = _timeSource.Describe();
        var routingDecision = ResolveRoutingDecision();
        var models = new[] { MapModelRef(routingDecision.SelectedCandidate!) };
        var tools = ResolveTools();
        var policyDecisions = ResolvePolicyDecisions();

        var entry = new EventLogEntry(
            RunId: runId,
            EventType: "workflow.hello",
            Data: new { Message = "hello", ManifestVersion = SchemaVersions.CurrentManifestVersion },
            OccurredAtUtc: now);

        var eventLogRecords = _eventLogIntegrityChain.SignEntries([entry]);
        var manifest = new Manifest(
            ManifestVersion: SchemaVersions.CurrentManifestVersion,
            RunId: runId,
            Seed: seed,
            TimeSource: timeSourceInfo,
            Models: models,
            Tools: tools,
            PolicyDecisions: policyDecisions,
            RoutingDecisions: [routingDecision],
            EventLog: BuildEventLogSummary(eventLogRecords),
            StartedAtUtc: now,
            CompletedAtUtc: now);

        var manifestErrors = ManifestValidator.Validate(manifest);
        if (manifestErrors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", manifestErrors));
        }

        return new HelloWorkflowArtifacts(
            ManifestRecord: _manifestSigner.SignManifest(manifest),
            EventLogRecords: eventLogRecords);
    }

    private static EventLogSummary BuildEventLogSummary(IReadOnlyList<EventLogRecord> eventLogRecords)
    {
        if (eventLogRecords.Count == 0)
        {
            throw new InvalidOperationException("Hello workflow must emit at least one event-log record.");
        }

        return new EventLogSummary(
            SchemaVersion: eventLogRecords[0].SchemaVersion,
            RecordCount: eventLogRecords.Count,
            LastChainMac: eventLogRecords[^1].Integrity.ChainMac);
    }

    private RouterSelectionResult ResolveRoutingDecision()
    {
        var routing = _options.Routing;
        var taskClass = RequireValue(routing.TaskClass, "HelloWorkflow.Routing.TaskClass");
        var result = _routerService.SelectModel(new RouterSelectionRequest(
            TaskClass: taskClass,
            MaxLatencyMs: routing.MaxLatencyMs,
            MaxCostPer1KTokens: routing.MaxCostPer1KTokens,
            MinQualityScore: routing.MinQualityScore,
            RequiredComplianceTags: routing.RequiredComplianceTags));

        if (result.SelectedCandidate is null)
        {
            throw new InvalidOperationException(
                $"Router did not select a model for HelloWorkflow.Routing.TaskClass '{taskClass}'.");
        }

        return result;
    }

    private static ModelRef MapModelRef(RouterModelCandidate candidate)
    {
        return new ModelRef(
            ModelId: candidate.ModelId,
            Provider: candidate.Provider,
            Version: candidate.Version);
    }

    private IReadOnlyList<ToolRef> ResolveTools()
    {
        if (_options.Tools.Count == 0)
        {
            throw new InvalidOperationException("HelloWorkflow.Tools must contain at least one entry.");
        }

        return _options.Tools.Select(tool => new ToolRef(
                RequireValue(tool.ToolId, "HelloWorkflow.Tools[].ToolId"),
                RequireValue(tool.Version, "HelloWorkflow.Tools[].Version")))
            .ToArray();
    }

    private IReadOnlyList<PolicyDecision> ResolvePolicyDecisions()
    {
        if (_options.PolicyDecisions.Count == 0)
        {
            throw new InvalidOperationException("HelloWorkflow.PolicyDecisions must contain at least one entry.");
        }

        return _options.PolicyDecisions.Select(policy => new PolicyDecision(
                RequireValue(policy.PolicyId, "HelloWorkflow.PolicyDecisions[].PolicyId"),
                RequireValue(policy.Decision, "HelloWorkflow.PolicyDecisions[].Decision"),
                policy.Reason))
            .ToArray();
    }

    private static string RequireValue(string? value, string path)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{path} is required.");
        }

        return value;
    }
}
