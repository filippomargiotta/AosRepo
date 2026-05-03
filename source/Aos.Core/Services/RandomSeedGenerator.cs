using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public sealed class RandomSeedGenerator : ISeedGenerator
{
    public SeedInfo CreateSeed(string runId)
    {
        var value = Random.Shared.NextInt64(1, long.MaxValue);

        return new SeedInfo(
            SeedId: $"seed-{runId}",
            Algorithm: "system-random-int64",
            Value: value,
            Derivation: "locked-per-run");
    }
}
