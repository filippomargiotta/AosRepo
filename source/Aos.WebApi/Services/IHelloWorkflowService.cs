using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public interface IHelloWorkflowService
{
    HelloWorkflowArtifacts CreateHelloArtifacts(string runId);
}

public sealed record HelloWorkflowArtifacts(
    Manifest Manifest,
    IReadOnlyList<EventLogRecord> EventLogRecords
);
