using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public sealed class RecordingTimeSource : ITimeSource
{
    private readonly ITimeSource _inner;
    private readonly List<DateTimeOffset> _recordedInstants = [];

    public RecordingTimeSource(ITimeSource inner)
    {
        _inner = inner;
    }

    public DateTimeOffset NowUtc()
    {
        var instant = _inner.NowUtc();
        _recordedInstants.Add(instant);
        return instant;
    }

    public TimeSourceInfo Describe()
    {
        var inner = _inner.Describe();
        var notes = string.IsNullOrWhiteSpace(inner.Notes)
            ? "time recording enabled"
            : $"{inner.Notes}; time recording enabled";

        return inner with
        {
            Mode = "record",
            Notes = notes
        };
    }

    public IReadOnlyList<DateTimeOffset> GetRecordedInstants() => _recordedInstants.ToArray();
}
