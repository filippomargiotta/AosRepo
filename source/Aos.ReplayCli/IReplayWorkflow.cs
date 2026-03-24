using Aos.WebApi.Models;
using Aos.WebApi.Services;

namespace Aos.ReplayCli;

internal interface IReplayWorkflow
{
    string WorkflowName { get; }

    ReplayWorkflowArtifacts Replay(
        Manifest manifest,
        IReadOnlyList<EventLogRecord> expectedRecords,
        IEventLogIntegrityChain eventLogIntegrityChain);
}

internal sealed record ReplayWorkflowArtifacts(
    Manifest Manifest,
    IReadOnlyList<EventLogRecord> EventLogRecords
);
