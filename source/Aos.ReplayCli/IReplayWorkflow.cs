using Aos.WebApi.Models;
using Aos.WebApi.Services;

namespace Aos.ReplayCli;

internal interface IReplayWorkflow
{
    string WorkflowName { get; }

    ReplayWorkflowArtifacts Replay(
        Manifest manifest,
        IReadOnlyList<EventLogRecord> expectedRecords,
        IEventLogIntegrityChain eventLogIntegrityChain,
        IManifestSigner manifestSigner);
}

internal sealed record ReplayWorkflowArtifacts(
    ManifestRecord ManifestRecord,
    IReadOnlyList<EventLogRecord> EventLogRecords
)
{
    public Manifest Manifest => ManifestRecord.Manifest;
}
