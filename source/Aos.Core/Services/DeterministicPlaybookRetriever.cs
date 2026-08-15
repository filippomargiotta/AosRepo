using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public sealed class DeterministicPlaybookRetriever
{
    private readonly IPlaybookStore _store;

    public DeterministicPlaybookRetriever(IPlaybookStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public IReadOnlyList<PlaybookMatch> Retrieve(PlaybookRetrievalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.TaskClass))
        {
            throw new ArgumentException("Task class is required.", nameof(request));
        }

        if (request.Terms is null)
        {
            throw new ArgumentException("Retrieval terms are required.", nameof(request));
        }

        if (request.MaxResults <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Max results must be greater than zero.");
        }

        var queryTerms = request.Terms
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Select(InMemoryPlaybookStore.NormalizeTerm)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(term => term, StringComparer.Ordinal)
            .ToArray();

        var matches = _store.ListByTaskClass(request.TaskClass)
            .Select(playbook => CreateMatch(playbook, queryTerms))
            .Where(match => queryTerms.Length == 0 || match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Playbook.PlaybookId, StringComparer.Ordinal)
            .ThenBy(match => match.Playbook.Version, StringComparer.Ordinal)
            .Take(request.MaxResults)
            .ToArray();

        return matches;
    }

    private static PlaybookMatch CreateMatch(PlannerPlaybook playbook, IReadOnlyList<string> queryTerms)
    {
        var matchedTerms = queryTerms
            .Intersect(playbook.RetrievalTerms, StringComparer.Ordinal)
            .OrderBy(term => term, StringComparer.Ordinal)
            .ToArray();

        return new PlaybookMatch(playbook, matchedTerms.Length, matchedTerms);
    }
}
