using Aos.WebApi.Models;

namespace Aos.ReplayCli;

internal interface IReplayWorkflow
{
    string WorkflowName { get; }

    ReplayWorkflowArtifacts Replay(Manifest manifest, IReadOnlyList<EventLogEntry> expectedEntries);
}

internal sealed record ReplayWorkflowArtifacts(
    Manifest Manifest,
    IReadOnlyList<EventLogEntry> EventLogEntries
);
