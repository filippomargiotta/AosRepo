using Aos.WebApi.Models;
using System.Collections.ObjectModel;

namespace Aos.WebApi.Services;

public sealed class DeterministicPlannerService : IPlannerService
{
    private readonly DeterministicPlaybookRetriever _retriever;
    private readonly IPlannerCandidateProvider _candidateProvider;
    private readonly PlannerPlanValidator _validator;

    public DeterministicPlannerService(
        DeterministicPlaybookRetriever retriever,
        IPlannerCandidateProvider candidateProvider,
        PlannerPlanValidator validator)
    {
        _retriever = retriever ?? throw new ArgumentNullException(nameof(retriever));
        _candidateProvider = candidateProvider ?? throw new ArgumentNullException(nameof(candidateProvider));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public PlannerPlanningResult Plan(PlannerTaskRequest task)
    {
        ValidateTask(task);
        var normalizedTask = SnapshotTask(task);
        var selected = _retriever.Retrieve(
            new PlaybookRetrievalRequest(normalizedTask.TaskClass, normalizedTask.Terms, 1)).SingleOrDefault();
        if (selected is null)
        {
            return Failed(normalizedTask, "none", null, "planner.playbook.not_found", "No matching playbook was found.");
        }

        var candidate = _candidateProvider.CreateCandidate(normalizedTask, selected);
        if (candidate.Plan is null)
        {
            return Failed(normalizedTask, candidate.Source, selected, candidate.ErrorCode!, candidate.Error!);
        }

        var validation = _validator.Validate(candidate.Plan);
        if (!string.Equals(candidate.Plan.TaskClass, normalizedTask.TaskClass, StringComparison.Ordinal))
        {
            validation = new PlannerValidationResult(
                false,
                validation.Errors
                    .Append(new PlannerValidationError(
                        "plan.task_class.mismatch",
                        "taskClass",
                        $"Plan task class '{candidate.Plan.TaskClass}' does not match requested task class '{normalizedTask.TaskClass}'."))
                    .ToArray());
        }

        return new PlannerPlanningResult(
            validation.IsValid ? "validated" : "rejected",
            normalizedTask,
            selected,
            candidate.Source,
            candidate.Plan,
            validation,
            validation.IsValid ? null : "planner.plan.invalid",
            validation.IsValid ? null : "Candidate plan failed validation.");
    }

    private static PlannerTaskRequest SnapshotTask(PlannerTaskRequest task)
    {
        var terms = task.Terms
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Select(InMemoryPlaybookStore.NormalizeTerm)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(term => term, StringComparer.Ordinal)
            .ToArray();
        var arguments = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in task.Arguments)
        {
            arguments[pair.Key] = pair.Value;
        }

        return task with
        {
            Terms = Array.AsReadOnly(terms),
            Arguments = new ReadOnlyDictionary<string, string>(arguments)
        };
    }

    private static PlannerPlanningResult Failed(
        PlannerTaskRequest task,
        string source,
        PlaybookMatch? selected,
        string errorCode,
        string error) =>
        new(
            "rejected",
            task,
            selected,
            source,
            null,
            new PlannerValidationResult(false, []),
            errorCode,
            error);

    private static void ValidateTask(PlannerTaskRequest task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (string.IsNullOrWhiteSpace(task.TaskId))
        {
            throw new ArgumentException("Task id is required.", nameof(task));
        }

        if (string.IsNullOrWhiteSpace(task.TaskClass))
        {
            throw new ArgumentException("Task class is required.", nameof(task));
        }

        if (task.Terms is null || task.Arguments is null)
        {
            throw new ArgumentException("Task terms and arguments are required.", nameof(task));
        }

        if (task.Arguments.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
        {
            throw new ArgumentException("Task argument names and values are required.", nameof(task));
        }
    }
}
