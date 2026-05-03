using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public interface ISeedGenerator
{
    SeedInfo CreateSeed(string runId);
}
