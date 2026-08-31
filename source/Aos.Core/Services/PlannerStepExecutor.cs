using System.Text.Json;
using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public sealed class PlannerStepExecutor
{
    private readonly AllowedActionCatalog _catalog;
    private readonly ICapabilityTokenIssuer _capabilityTokenIssuer;
    private readonly IToolExecutor _toolExecutor;
    private readonly ITimeSource _timeSource;

    public PlannerStepExecutor(
        AllowedActionCatalog catalog,
        ICapabilityTokenIssuer capabilityTokenIssuer,
        IToolExecutor toolExecutor,
        ITimeSource timeSource)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _capabilityTokenIssuer = capabilityTokenIssuer ?? throw new ArgumentNullException(nameof(capabilityTokenIssuer));
        _toolExecutor = toolExecutor ?? throw new ArgumentNullException(nameof(toolExecutor));
        _timeSource = timeSource ?? throw new ArgumentNullException(nameof(timeSource));
    }

    public PlannerExecutionResult Execute(string runId, PlannerPlan plan, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("Run id is required.", nameof(runId));
        }

        ArgumentNullException.ThrowIfNull(plan);
        var completed = new List<PlannerStepExecutionResult>();
        foreach (var step in plan.Steps)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new PlannerExecutionResult("cancelled", plan, completed.AsReadOnly(), "execution_cancelled");
            }

            if (!_catalog.TryGet(step.ActionId, out var action))
            {
                throw new InvalidOperationException($"Validated action '{step.ActionId}' is missing from the action catalogue.");
            }

            var requestedAt = _timeSource.NowUtc();
            var invocationId = $"{runId}:planner-step:{step.Sequence}";
            var scope = new ToolCapabilityScope(
                runId,
                invocationId,
                action.Tool.ToolId,
                action.Tool.Version,
                action.ActionId);
            var token = _capabilityTokenIssuer.Issue(scope, requestedAt);
            var input = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in step.Arguments)
            {
                input[pair.Key] = pair.Value;
            }
            var result = _toolExecutor.Execute(new ToolExecutionRequest(
                runId,
                invocationId,
                action.Tool,
                action.ActionId,
                JsonSerializer.Serialize(input),
                token,
                requestedAt));
            completed.Add(new PlannerStepExecutionResult(
                step.Sequence,
                step.StepId,
                step.ActionId,
                requestedAt,
                result));

            if (!string.Equals(result.Status, "succeeded", StringComparison.Ordinal))
            {
                return new PlannerExecutionResult(
                    "failed",
                    plan,
                    completed.AsReadOnly(),
                    result.Error ?? $"tool_{result.Status}");
            }
        }

        return new PlannerExecutionResult("succeeded", plan, completed.AsReadOnly(), null);
    }
}
