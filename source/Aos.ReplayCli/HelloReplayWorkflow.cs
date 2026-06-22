using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Aos.WebApi.Services;

namespace Aos.ReplayCli;

internal sealed class HelloReplayWorkflow : IReplayWorkflow
{
    private const string ReplayCapabilityKey = "replay-capability-key-at-least-32-bytes";

    public string WorkflowName => "hello";

    public ReplayWorkflowArtifacts Replay(
        Manifest manifest,
        IReadOnlyList<EventLogRecord> expectedRecords,
        IEventLogIntegrityChain eventLogIntegrityChain,
        IManifestSigner manifestSigner)
    {
        var replayTimeSource = new ReplayTimeSource(
            [ manifest.StartedAtUtc, .. expectedRecords.Select(record => record.Entry.OccurredAtUtc) ],
            manifest.TimeSource);
        var capabilityTokenService = CreateCapabilityTokenService();
        var service = new HelloWorkflowService(
            new FixedSeedProvider(manifest.Seed),
            replayTimeSource,
            Microsoft.Extensions.Options.Options.Create(CreateOptionsFromManifest(manifest)),
            new FixedRouterService(manifest.RoutingDecisions.Single()),
            capabilityTokenService,
            new CapabilityEnforcingToolExecutor(
                capabilityTokenService,
                new DeterministicEchoToolExecutor()),
            eventLogIntegrityChain,
            manifestSigner);

        var artifacts = service.CreateHelloArtifacts(manifest.RunId);
        return new ReplayWorkflowArtifacts(artifacts.ManifestRecord, artifacts.EventLogRecords);
    }

    private static HelloWorkflowOptions CreateOptionsFromManifest(Manifest manifest)
    {
        var routingDecision = manifest.RoutingDecisions.Single();
        return new HelloWorkflowOptions
        {
            Routing = new HelloWorkflowRoutingOptions
            {
                TaskClass = routingDecision.TaskClass,
                MaxLatencyMs = routingDecision.Policy.EffectiveConstraints.MaxLatencyMs,
                MaxCostPer1KTokens = routingDecision.Policy.EffectiveConstraints.MaxCostPer1KTokens,
                MinQualityScore = routingDecision.Policy.EffectiveConstraints.MinQualityScore,
                RequiredComplianceTags = routingDecision.Policy.EffectiveConstraints.RequiredComplianceTags.ToList()
            },
            Models = manifest.Models
                .Select(model => new HelloWorkflowModelOptions
                {
                    ModelId = model.ModelId,
                    Provider = model.Provider,
                    Version = model.Version
                })
                .ToList(),
            Tools = manifest.Tools
                .Select(tool => new HelloWorkflowToolOptions
                {
                    ToolId = tool.ToolId,
                    Version = tool.Version
                })
                .ToList(),
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

    private static HmacJwtCapabilityTokenService CreateCapabilityTokenService() =>
        new(Microsoft.Extensions.Options.Options.Create(new CapabilityTokenOptions
        {
            Issuer = "aos.replay",
            Audience = "aos.tools",
            SigningKey = ReplayCapabilityKey,
            KeyId = "replay-capability-v1",
            LifetimeSeconds = 300
        }));

    private sealed class FixedSeedProvider : ISeedProvider
    {
        private readonly SeedInfo _seed;

        public FixedSeedProvider(SeedInfo seed)
        {
            _seed = seed;
        }

        public SeedInfo GetLockedSeed(string runId) => _seed;
    }

    private sealed class FixedRouterService : IRouterService
    {
        private readonly RouterSelectionResult _routingDecision;

        public FixedRouterService(RouterSelectionResult routingDecision)
        {
            _routingDecision = routingDecision;
        }

        public RouterSelectionResult SelectModel(RouterSelectionRequest request)
        {
            if (!string.Equals(request.TaskClass, _routingDecision.TaskClass, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Replay requested task class '{request.TaskClass}', expected '{_routingDecision.TaskClass}'.");
            }

            return _routingDecision;
        }
    }
}
