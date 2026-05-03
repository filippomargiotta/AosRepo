using System.Collections.Concurrent;
using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public sealed class LockedSeedProvider : ISeedProvider
{
    private readonly ISeedGenerator _generator;
    private readonly ConcurrentDictionary<string, SeedInfo> _seeds = new(StringComparer.Ordinal);

    public LockedSeedProvider(ISeedGenerator generator)
    {
        _generator = generator;
    }

    public SeedInfo GetLockedSeed(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("Run id is required.", nameof(runId));
        }

        return _seeds.GetOrAdd(runId, _generator.CreateSeed);
    }
}
