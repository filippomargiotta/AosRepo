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
    private readonly IManifestWriter _manifestWriter;
    private readonly IHelloWorkflowService _helloWorkflowService;
    private readonly IPlannerWorkflowService _plannerWorkflowService;
    private readonly ILogger<WorkflowController> _logger;

    public WorkflowController(
        IEventLogWriter eventLogWriter,
        IManifestWriter manifestWriter,
        IHelloWorkflowService helloWorkflowService,
        IPlannerWorkflowService plannerWorkflowService,
        ILogger<WorkflowController> logger)
    {
        _eventLogWriter = eventLogWriter;
        _manifestWriter = manifestWriter;
        _helloWorkflowService = helloWorkflowService;
        _plannerWorkflowService = plannerWorkflowService;
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

        await _manifestWriter.WriteAsync(artifacts.ManifestRecord, cancellationToken);

        foreach (var record in artifacts.EventLogRecords)
        {
            await _eventLogWriter.WriteAsync(record, cancellationToken);
        }

        _logger.LogInformation("Completed workflow hello for run {RunId}", runId);

        return Ok(new
        {
            RunId = runId,
            Manifest = artifacts.ManifestRecord
        });
    }

    [HttpPost("planner")]
    public async Task<IActionResult> Planner(
        [FromBody] PlannerTaskRequest task,
        CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid().ToString("N");
        _logger.LogInformation("Starting workflow planner for run {RunId} and task {TaskId}", runId, task.TaskId);
        Activity.Current?.SetTag("aos.run_id", runId);
        Activity.Current?.SetTag("aos.workflow", "planner");

        PlannerWorkflowArtifacts artifacts;
        try
        {
            artifacts = _plannerWorkflowService.CreateArtifacts(runId, task, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning("Workflow planner rejected run {RunId}: {Error}", runId, ex.Message);
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        await _manifestWriter.WriteAsync(artifacts.ManifestRecord, cancellationToken);
        foreach (var record in artifacts.EventLogRecords)
        {
            await _eventLogWriter.WriteAsync(record, cancellationToken);
        }

        _logger.LogInformation(
            "Completed workflow planner for run {RunId} with status {Status}",
            runId,
            artifacts.Execution.Status);
        return Ok(new
        {
            RunId = runId,
            artifacts.Execution.Status,
            Plan = artifacts.Planning.Plan,
            Steps = artifacts.Execution.Steps
        });
    }
}
