using Aos.WebApi.Models;
using Aos.WebApi.Services;

namespace Aos.WebApi.Tests;

internal sealed class FixedSeedProvider : ISeedProvider
{
    private readonly SeedInfo _seed;
    private readonly bool _preserveSeedId;

    public FixedSeedProvider(SeedInfo seed, bool preserveSeedId = false)
    {
        _seed = seed;
        _preserveSeedId = preserveSeedId;
    }

    public SeedInfo GetLockedSeed(string runId) =>
        _preserveSeedId ? _seed : _seed with { SeedId = $"seed-{runId}" };
}

internal sealed class FixedTimeSource : ITimeSource
{
    private readonly DateTimeOffset _instant;
    private readonly TimeSourceInfo _descriptor;

    public FixedTimeSource(DateTimeOffset instant, TimeSourceInfo descriptor)
    {
        _instant = instant;
        _descriptor = descriptor;
    }

    public DateTimeOffset NowUtc() => _instant;

    public TimeSourceInfo Describe() => _descriptor;
}

internal sealed class FixedSequenceTimeSource : ITimeSource
{
    private readonly Queue<DateTimeOffset> _instants;
    private readonly TimeSourceInfo _descriptor;

    public FixedSequenceTimeSource(IEnumerable<DateTimeOffset> instants, TimeSourceInfo descriptor)
    {
        _instants = new Queue<DateTimeOffset>(instants);
        _descriptor = descriptor;
    }

    public DateTimeOffset NowUtc()
    {
        if (!_instants.TryDequeue(out var instant))
        {
            throw new InvalidOperationException("No more fixed instants available.");
        }

        return instant;
    }

    public TimeSourceInfo Describe() => _descriptor;
}

internal sealed class FixedRouterService : IRouterService
{
    private readonly RouterSelectionResult _routingResult;
    private readonly string? _expectedTaskClass;

    public FixedRouterService(RouterSelectionResult routingResult, string? expectedTaskClass = null)
    {
        _routingResult = routingResult;
        _expectedTaskClass = expectedTaskClass;
    }

    public RouterSelectionResult SelectModel(RouterSelectionRequest request)
    {
        if (_expectedTaskClass is not null &&
            !string.Equals(request.TaskClass, _expectedTaskClass, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unexpected task class '{request.TaskClass}', expected '{_expectedTaskClass}'.");
        }

        return _routingResult;
    }
}
