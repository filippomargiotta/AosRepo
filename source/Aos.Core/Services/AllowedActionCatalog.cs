using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public sealed class AllowedActionCatalog
{
    private readonly IReadOnlyDictionary<string, AllowedActionDefinition> _actionsById;

    public AllowedActionCatalog(IEnumerable<AllowedActionDefinition> allowedActions)
    {
        ArgumentNullException.ThrowIfNull(allowedActions);
        var actions = allowedActions.Select(ValidateAndSnapshot).ToArray();
        var duplicateIds = actions
            .GroupBy(action => action.ActionId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        if (duplicateIds.Length > 0)
        {
            throw new InvalidOperationException(
                $"Allowed actions contain duplicate action ids: {string.Join(", ", duplicateIds)}.");
        }

        _actionsById = actions.ToDictionary(action => action.ActionId, StringComparer.Ordinal);
        Actions = Array.AsReadOnly(actions.OrderBy(action => action.ActionId, StringComparer.Ordinal).ToArray());
    }

    public IReadOnlyList<AllowedActionDefinition> Actions { get; }

    public bool TryGet(string actionId, out AllowedActionDefinition action) =>
        _actionsById.TryGetValue(actionId, out action!);

    private static AllowedActionDefinition ValidateAndSnapshot(AllowedActionDefinition action)
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

        var arguments = action.Arguments.Select((argument, index) =>
        {
            if (argument is null || string.IsNullOrWhiteSpace(argument.Name))
            {
                throw new InvalidOperationException(
                    $"Allowed action '{action.ActionId}' argument at index {index} requires a name.");
            }

            return argument;
        }).ToArray();
        var duplicates = arguments
            .GroupBy(argument => argument.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"Allowed action '{action.ActionId}' contains duplicate arguments: {string.Join(", ", duplicates)}.");
        }

        return action with { Arguments = Array.AsReadOnly(arguments) };
    }
}
