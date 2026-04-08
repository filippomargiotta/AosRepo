using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public sealed class ReplayTimeSource : ITimeSource
{
    private readonly Queue<DateTimeOffset> _remainingInstants;
    private readonly TimeSourceInfo _descriptor;

    public ReplayTimeSource(IEnumerable<DateTimeOffset> recordedInstants, TimeSourceInfo? descriptor = null)
    {
        _remainingInstants = new Queue<DateTimeOffset>(recordedInstants);
        _descriptor = descriptor ?? new TimeSourceInfo(
            Mode: "replay",
            Source: "recorded-sequence",
            ClockId: "clock-replay-1",
            Precision: "utc-millis",
            Notes: "replayed from in-memory sequence");
    }

    public DateTimeOffset NowUtc()
    {
        if (!_remainingInstants.TryDequeue(out var instant))
        {
            throw new InvalidOperationException("Replay time source has no more recorded instants.");
        }

        return instant;
    }

    public TimeSourceInfo Describe() => _descriptor;
}
