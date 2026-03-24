using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Aos.WebApi.Services;

namespace Aos.ReplayCli;

internal sealed class HelloReplayWorkflow : IReplayWorkflow
{
    public string WorkflowName => "hello";

    public ReplayWorkflowArtifacts Replay(
        Manifest manifest,
        IReadOnlyList<EventLogRecord> expectedRecords,
        IEventLogIntegrityChain eventLogIntegrityChain)
    {
        var replayTimeSource = new ReplayTimeSource(expectedRecords.Select(record => record.Entry.OccurredAtUtc));
        var service = new HelloWorkflowService(
            new FixedSeedProvider(manifest.Seed),
            replayTimeSource,
            Microsoft.Extensions.Options.Options.Create(CreateOptionsFromManifest(manifest)),
            eventLogIntegrityChain);

        var artifacts = service.CreateHelloArtifacts(manifest.RunId);
        return new ReplayWorkflowArtifacts(artifacts.Manifest, artifacts.EventLogRecords);
    }

    private static HelloWorkflowOptions CreateOptionsFromManifest(Manifest manifest)
    {
        return new HelloWorkflowOptions
        {
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
                .Select(policy => new HelloWorkflowPolicyOptions
                {
                    PolicyId = policy.PolicyId,
                    Decision = policy.Decision,
                    Reason = policy.Reason
                })
                .ToList()
        };
    }

    private sealed class FixedSeedProvider : ISeedProvider
    {
        private readonly SeedInfo _seed;

        public FixedSeedProvider(SeedInfo seed)
        {
            _seed = seed;
        }

        public SeedInfo GetLockedSeed(string runId) => _seed;
    }
}
