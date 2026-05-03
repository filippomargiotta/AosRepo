using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public interface ITimeSource
{
    DateTimeOffset NowUtc();
    TimeSourceInfo Describe();
}
