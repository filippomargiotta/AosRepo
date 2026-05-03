using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public interface IHelloWorkflowService
{
    HelloWorkflowArtifacts CreateHelloArtifacts(string runId);
}

public sealed record HelloWorkflowArtifacts(
    ManifestRecord ManifestRecord,
    IReadOnlyList<EventLogRecord> EventLogRecords
)
{
    public Manifest Manifest => ManifestRecord.Manifest;
}
