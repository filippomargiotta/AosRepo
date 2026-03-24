using System.Diagnostics;
using Aos.WebApi.Models;
using Aos.WebApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace Aos.WebApi.Controllers;

[ApiController]
[Route("workflow")]
public class WorkflowController : ControllerBase
{
    private readonly IEventLogWriter _eventLogWriter;
    private readonly IHelloWorkflowService _helloWorkflowService;
    private readonly ILogger<WorkflowController> _logger;

    public WorkflowController(
        IEventLogWriter eventLogWriter,
        IHelloWorkflowService helloWorkflowService,
        ILogger<WorkflowController> logger)
    {
        _eventLogWriter = eventLogWriter;
        _helloWorkflowService = helloWorkflowService;
        _logger = logger;
    }

    [HttpPost("hello")]
    public async Task<IActionResult> Hello(CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid().ToString("N");

        _logger.LogInformation("Starting workflow hello for run {RunId}", runId);
        Activity.Current?.SetTag("aos.run_id", runId);
        Activity.Current?.SetTag("aos.workflow", "hello");

        HelloWorkflowArtifacts artifacts;
        try
        {
            artifacts = _helloWorkflowService.CreateHelloArtifacts(runId);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError("Workflow hello failed validation for run {RunId}: {Error}", runId, ex.Message);
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }

        _logger.LogInformation(
            "Manifest validated for run {RunId} with version {Version}",
            runId,
            artifacts.Manifest.ManifestVersion);

        foreach (var record in artifacts.EventLogRecords)
        {
            await _eventLogWriter.WriteAsync(record, cancellationToken);
        }

        _logger.LogInformation("Completed workflow hello for run {RunId}", runId);

        return Ok(new
        {
            RunId = runId,
            Manifest = artifacts.Manifest
        });
    }
}
