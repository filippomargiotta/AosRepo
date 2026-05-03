using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public interface ISeedProvider
{
    SeedInfo GetLockedSeed(string runId);
}
