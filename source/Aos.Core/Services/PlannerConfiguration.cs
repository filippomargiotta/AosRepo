using Aos.WebApi.Models;
using Aos.WebApi.Options;

namespace Aos.WebApi.Services;

public static class PlannerConfiguration
{
    public static IReadOnlyList<AllowedActionDefinition> CreateAllowedActions(PlannerWorkflowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.AllowedActions.Select(action => new AllowedActionDefinition(
            action.ActionId,
            new ToolRef(action.ToolId, action.ToolVersion),
            action.Arguments.Select(argument =>
                new AllowedActionArgumentDefinition(argument.Name, argument.Required)).ToArray())).ToArray();
    }

    public static IReadOnlyList<PlannerPlaybook> CreatePlaybooks(PlannerWorkflowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Playbooks.Select(playbook => new PlannerPlaybook(
            playbook.SchemaVersion,
            playbook.PlaybookId,
            playbook.Version,
            playbook.TaskClass,
            playbook.Description,
            playbook.RetrievalTerms.ToArray(),
            playbook.Steps.Select(step => new PlannerPlaybookStep(
                step.ActionId,
                new Dictionary<string, string>(step.ArgumentTemplates, StringComparer.Ordinal))).ToArray())).ToArray();
    }
}
