using System.Collections.ObjectModel;
using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public sealed class InMemoryPlaybookStore : IPlaybookStore
{
    private readonly IReadOnlyList<PlannerPlaybook> _playbooks;

    public InMemoryPlaybookStore(IEnumerable<PlannerPlaybook> playbooks)
    {
        ArgumentNullException.ThrowIfNull(playbooks);

        var snapshot = playbooks.Select(ValidateAndSnapshot).ToArray();
        var duplicateKeys = snapshot
            .GroupBy(playbook => (playbook.PlaybookId, playbook.Version))
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key.PlaybookId}/{group.Key.Version}")
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        if (duplicateKeys.Length > 0)
        {
            throw new InvalidOperationException(
                $"Playbooks contain duplicate id/version entries: {string.Join(", ", duplicateKeys)}.");
        }

        _playbooks = Array.AsReadOnly(snapshot
            .OrderBy(playbook => playbook.TaskClass, StringComparer.Ordinal)
            .ThenBy(playbook => playbook.PlaybookId, StringComparer.Ordinal)
            .ThenBy(playbook => playbook.Version, StringComparer.Ordinal)
            .ToArray());
    }

    public IReadOnlyList<PlannerPlaybook> ListByTaskClass(string taskClass)
    {
        if (string.IsNullOrWhiteSpace(taskClass))
        {
            throw new ArgumentException("Task class is required.", nameof(taskClass));
        }

        return _playbooks
            .Where(playbook => string.Equals(playbook.TaskClass, taskClass, StringComparison.Ordinal))
            .ToArray();
    }

    private static PlannerPlaybook ValidateAndSnapshot(PlannerPlaybook playbook)
    {
        ArgumentNullException.ThrowIfNull(playbook);

        if (!PlannerSchemaVersions.IsSupportedPlaybookVersion(playbook.SchemaVersion))
        {
            throw new InvalidOperationException(
                $"Playbook '{playbook.PlaybookId}' schema version '{playbook.SchemaVersion}' is not supported. Supported version: {PlannerSchemaVersions.CurrentPlaybookVersion}.");
        }

        if (string.IsNullOrWhiteSpace(playbook.PlaybookId))
        {
            throw new InvalidOperationException("Playbooks[].PlaybookId is required.");
        }

        if (string.IsNullOrWhiteSpace(playbook.Version))
        {
            throw new InvalidOperationException($"Playbook '{playbook.PlaybookId}' requires a version.");
        }

        if (string.IsNullOrWhiteSpace(playbook.TaskClass))
        {
            throw new InvalidOperationException($"Playbook '{playbook.PlaybookId}' requires a task class.");
        }

        if (string.IsNullOrWhiteSpace(playbook.Description))
        {
            throw new InvalidOperationException($"Playbook '{playbook.PlaybookId}' requires a description.");
        }

        if (playbook.RetrievalTerms is null)
        {
            throw new InvalidOperationException($"Playbook '{playbook.PlaybookId}' requires retrieval terms.");
        }

        if (playbook.Steps is null || playbook.Steps.Count == 0)
        {
            throw new InvalidOperationException($"Playbook '{playbook.PlaybookId}' requires at least one step.");
        }

        var terms = playbook.RetrievalTerms
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Select(NormalizeTerm)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(term => term, StringComparer.Ordinal)
            .ToArray();

        var steps = playbook.Steps.Select((step, index) =>
        {
            if (step is null || string.IsNullOrWhiteSpace(step.ActionId))
            {
                throw new InvalidOperationException(
                    $"Playbook '{playbook.PlaybookId}' step at index {index} requires an action id.");
            }

            if (step.ArgumentTemplates is null)
            {
                throw new InvalidOperationException(
                    $"Playbook '{playbook.PlaybookId}' step at index {index} requires argument templates.");
            }

            if (step.ArgumentTemplates.Any(pair =>
                    string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
            {
                throw new InvalidOperationException(
                    $"Playbook '{playbook.PlaybookId}' step at index {index} contains an invalid argument template.");
            }

            return step with
            {
                ArgumentTemplates = new ReadOnlyDictionary<string, string>(
                    new Dictionary<string, string>(step.ArgumentTemplates, StringComparer.Ordinal))
            };
        }).ToArray();

        return playbook with
        {
            RetrievalTerms = Array.AsReadOnly(terms),
            Steps = Array.AsReadOnly(steps)
        };
    }

    internal static string NormalizeTerm(string term) => term.Trim().ToLowerInvariant();
}
