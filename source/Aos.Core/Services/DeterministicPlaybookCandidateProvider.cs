using System.Collections.ObjectModel;
using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public sealed class DeterministicPlaybookCandidateProvider : IPlannerCandidateProvider
{
    public const string SourceId = "playbook-v1";

    public PlannerCandidateResult CreateCandidate(PlannerTaskRequest task, PlaybookMatch selectedPlaybook)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(selectedPlaybook);

        var steps = new List<PlannerPlanStep>(selectedPlaybook.Playbook.Steps.Count);
        for (var index = 0; index < selectedPlaybook.Playbook.Steps.Count; index++)
        {
            var template = selectedPlaybook.Playbook.Steps[index];
            var arguments = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in template.ArgumentTemplates.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!TryResolveTemplate(pair.Value, task.Arguments, out var value, out var missingArgument))
                {
                    return new PlannerCandidateResult(
                        SourceId,
                        null,
                        "planner.template.argument_missing",
                        $"Template argument '{missingArgument}' is missing for step {index + 1}.");
                }

                arguments[pair.Key] = value!;
            }

            steps.Add(new PlannerPlanStep(
                Sequence: index + 1,
                StepId: $"step-{index + 1}",
                ActionId: template.ActionId,
                Arguments: new ReadOnlyDictionary<string, string>(arguments)));
        }

        return new PlannerCandidateResult(
            SourceId,
            new PlannerPlan(
                PlannerSchemaVersions.CurrentPlanVersion,
                $"{task.TaskId}:plan:1",
                task.TaskClass,
                steps.AsReadOnly()),
            null,
            null);
    }

    private static bool TryResolveTemplate(
        string template,
        IReadOnlyDictionary<string, string> bindings,
        out string? value,
        out string? missingArgument)
    {
        value = template;
        missingArgument = null;
        if (template.Length < 5 || !template.StartsWith("{{", StringComparison.Ordinal) ||
            !template.EndsWith("}}", StringComparison.Ordinal))
        {
            return true;
        }

        var name = template[2..^2];
        if (string.IsNullOrWhiteSpace(name) || !bindings.TryGetValue(name, out value))
        {
            missingArgument = name;
            value = null;
            return false;
        }

        return true;
    }
}
