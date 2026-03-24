using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Microsoft.Extensions.Options;

namespace Aos.WebApi.Services;

public sealed class HelloWorkflowService : IHelloWorkflowService
{
    private readonly ISeedProvider _seedProvider;
    private readonly ITimeSource _timeSource;
    private readonly HelloWorkflowOptions _options;
    private readonly IEventLogIntegrityChain _eventLogIntegrityChain;

    public HelloWorkflowService(
        ISeedProvider seedProvider,
        ITimeSource timeSource,
        IOptions<HelloWorkflowOptions> options,
        IEventLogIntegrityChain eventLogIntegrityChain)
    {
        _seedProvider = seedProvider;
        _timeSource = timeSource;
        _options = options.Value;
        _eventLogIntegrityChain = eventLogIntegrityChain;
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
        var models = ResolveModels();
        var tools = ResolveTools();
        var policyDecisions = ResolvePolicyDecisions();

        var manifest = new Manifest(
            ManifestVersion: SchemaVersions.CurrentManifestVersion,
            RunId: runId,
            Seed: seed,
            TimeSource: timeSourceInfo,
            Models: models,
            Tools: tools,
            PolicyDecisions: policyDecisions,
            StartedAtUtc: now,
            CompletedAtUtc: now);

        var manifestErrors = ManifestValidator.Validate(manifest);
        if (manifestErrors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", manifestErrors));
        }

        var entry = new EventLogEntry(
            RunId: runId,
            EventType: "workflow.hello",
            Data: new { Message = "hello", ManifestVersion = manifest.ManifestVersion },
            OccurredAtUtc: now);

        return new HelloWorkflowArtifacts(
            Manifest: manifest,
            EventLogRecords: _eventLogIntegrityChain.SignEntries([entry]));
    }

    private IReadOnlyList<ModelRef> ResolveModels()
    {
        if (_options.Models.Count == 0)
        {
            throw new InvalidOperationException("HelloWorkflow.Models must contain at least one entry.");
        }

        return _options.Models.Select(model => new ModelRef(
                RequireValue(model.ModelId, "HelloWorkflow.Models[].ModelId"),
                RequireValue(model.Provider, "HelloWorkflow.Models[].Provider"),
                RequireValue(model.Version, "HelloWorkflow.Models[].Version")))
            .ToArray();
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
