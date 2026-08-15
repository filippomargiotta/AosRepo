using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public sealed class PlannerPlanValidator
{
    private readonly IReadOnlyDictionary<string, AllowedActionDefinition> _actionsById;

    public PlannerPlanValidator(IEnumerable<AllowedActionDefinition> allowedActions)
    {
        ArgumentNullException.ThrowIfNull(allowedActions);

        var actions = allowedActions.Select(ValidateAndSnapshotActionDefinition).ToArray();

        var duplicateActionIds = actions
            .GroupBy(action => action.ActionId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(actionId => actionId, StringComparer.Ordinal)
            .ToArray();

        if (duplicateActionIds.Length > 0)
        {
            throw new InvalidOperationException(
                $"Allowed actions contain duplicate action ids: {string.Join(", ", duplicateActionIds)}.");
        }

        _actionsById = actions.ToDictionary(action => action.ActionId, StringComparer.Ordinal);
    }

    public PlannerValidationResult Validate(PlannerPlan? plan)
    {
        var errors = new List<PlannerValidationError>();
        if (plan is null)
        {
            AddError(errors, "plan.required", "$", "Plan is required.");
            return new PlannerValidationResult(false, errors);
        }

        if (!PlannerSchemaVersions.IsSupportedPlanVersion(plan.SchemaVersion))
        {
            AddError(
                errors,
                "plan.schema_version.unsupported",
                "schemaVersion",
                $"Plan schema version '{plan.SchemaVersion}' is not supported. Supported version: {PlannerSchemaVersions.CurrentPlanVersion}.");
        }

        if (string.IsNullOrWhiteSpace(plan.PlanId))
        {
            AddError(errors, "plan.id.required", "planId", "Plan id is required.");
        }

        if (string.IsNullOrWhiteSpace(plan.TaskClass))
        {
            AddError(errors, "plan.task_class.required", "taskClass", "Task class is required.");
        }

        if (plan.Steps is null || plan.Steps.Count == 0)
        {
            AddError(errors, "plan.steps.required", "steps", "At least one plan step is required.");
            return new PlannerValidationResult(false, errors);
        }

        var seenStepIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var step = plan.Steps[index];
            var path = $"steps[{index}]";

            if (step is null)
            {
                AddError(errors, "plan.step.required", path, "Plan step is required.");
                continue;
            }

            var expectedSequence = index + 1;
            if (step.Sequence != expectedSequence)
            {
                AddError(
                    errors,
                    "plan.step.sequence.invalid",
                    $"{path}.sequence",
                    $"Step sequence must be {expectedSequence} at index {index}.");
            }

            if (string.IsNullOrWhiteSpace(step.StepId))
            {
                AddError(errors, "plan.step.id.required", $"{path}.stepId", "Step id is required.");
            }
            else if (!seenStepIds.Add(step.StepId))
            {
                AddError(
                    errors,
                    "plan.step.id.duplicate",
                    $"{path}.stepId",
                    $"Step id '{step.StepId}' is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(step.ActionId))
            {
                AddError(errors, "plan.step.action.required", $"{path}.actionId", "Action id is required.");
                continue;
            }

            if (!_actionsById.TryGetValue(step.ActionId, out var action))
            {
                AddError(
                    errors,
                    "plan.step.action.not_allowed",
                    $"{path}.actionId",
                    $"Action '{step.ActionId}' is not allowed.");
                continue;
            }

            ValidateArguments(step, action, path, errors);
        }

        return new PlannerValidationResult(errors.Count == 0, errors);
    }

    private static void ValidateArguments(
        PlannerPlanStep step,
        AllowedActionDefinition action,
        string path,
        List<PlannerValidationError> errors)
    {
        if (step.Arguments is null)
        {
            AddError(
                errors,
                "plan.step.arguments.required",
                $"{path}.arguments",
                "Step arguments are required.");
            return;
        }

        var definitionsByName = action.Arguments.ToDictionary(argument => argument.Name, StringComparer.Ordinal);
        var unknownArguments = step.Arguments.Keys
            .Where(name => !definitionsByName.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        foreach (var argument in unknownArguments)
        {
            AddError(
                errors,
                "plan.step.argument.not_allowed",
                $"{path}.arguments.{argument}",
                $"Argument '{argument}' is not allowed for action '{action.ActionId}'.");
        }

        var missingRequiredArguments = action.Arguments
            .Where(definition => definition.Required &&
                (!step.Arguments.TryGetValue(definition.Name, out var value) || string.IsNullOrWhiteSpace(value)))
            .Select(definition => definition.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        foreach (var argument in missingRequiredArguments)
        {
            AddError(
                errors,
                "plan.step.argument.required",
                $"{path}.arguments.{argument}",
                $"Required argument '{argument}' is missing for action '{action.ActionId}'.");
        }
    }

    private static AllowedActionDefinition ValidateAndSnapshotActionDefinition(AllowedActionDefinition action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (string.IsNullOrWhiteSpace(action.ActionId))
        {
            throw new InvalidOperationException("AllowedActions[].ActionId is required.");
        }

        if (action.Tool is null || string.IsNullOrWhiteSpace(action.Tool.ToolId))
        {
            throw new InvalidOperationException($"Allowed action '{action.ActionId}' requires a tool id.");
        }

        if (string.IsNullOrWhiteSpace(action.Tool.Version))
        {
            throw new InvalidOperationException($"Allowed action '{action.ActionId}' requires a tool version.");
        }

        if (action.Arguments is null)
        {
            throw new InvalidOperationException($"Allowed action '{action.ActionId}' requires an argument definition list.");
        }

        var invalidArgumentIndex = action.Arguments
            .Select((argument, index) => new { argument, index })
            .FirstOrDefault(item => item.argument is null || string.IsNullOrWhiteSpace(item.argument.Name));
        if (invalidArgumentIndex is not null)
        {
            throw new InvalidOperationException(
                $"Allowed action '{action.ActionId}' argument at index {invalidArgumentIndex.index} requires a name.");
        }

        var duplicateArguments = action.Arguments
            .GroupBy(argument => argument.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (duplicateArguments.Length > 0)
        {
            throw new InvalidOperationException(
                $"Allowed action '{action.ActionId}' contains duplicate arguments: {string.Join(", ", duplicateArguments)}.");
        }

        return action with
        {
            Arguments = Array.AsReadOnly(action.Arguments.ToArray())
        };
    }

    private static void AddError(
        List<PlannerValidationError> errors,
        string code,
        string path,
        string message) =>
        errors.Add(new PlannerValidationError(code, path, message));
}
