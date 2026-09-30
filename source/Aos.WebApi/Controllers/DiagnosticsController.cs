using Aos.WebApi.Models;
using Aos.WebApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace Aos.WebApi.Controllers;

[ApiController]
[Route("workflow/diagnostics")]
public sealed class DiagnosticsController(DiagnosticsRuntime runtime, IManifestWriter manifests, IEventLogWriter logs) : ControllerBase
{
    [HttpGet("snapshot")]
    public IActionResult Snapshot() => Ok(runtime.Snapshot.Reference);

    [HttpPost]
    public async Task<IActionResult> Diagnose([FromBody] DiagnosticsRequest request, CancellationToken cancellationToken)
    {
        DiagnosticsWorkflowArtifacts artifacts;
        try
        {
            runtime.Snapshot.Verify(request.Snapshot);
            artifacts = runtime.Workflow.CreateArtifacts(Guid.NewGuid().ToString("N"), request, cancellationToken);
        }
        catch (RepositoryPolicyException ex) { return BadRequest(new { reasonCode = ex.ReasonCode }); }
        catch (ArgumentException ex) { return BadRequest(new { reasonCode = "request.invalid", error = ex.Message }); }
        await manifests.WriteAsync(artifacts.ManifestRecord, cancellationToken);
        foreach (var record in artifacts.EventLogRecords) await logs.WriteAsync(record, cancellationToken);
        return Ok(new { artifacts.ManifestRecord.Manifest.RunId, artifacts.Outcome });
    }
}
