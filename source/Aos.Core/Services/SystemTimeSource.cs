using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public sealed class SystemTimeSource : ITimeSource
{
    public DateTimeOffset NowUtc() => DateTimeOffset.UtcNow;

    public TimeSourceInfo Describe() => new(
        Mode: "record",
        Source: "system-utc",
        ClockId: "clock-1",
        Precision: "utc-millis",
        Notes: "recorded via injected time source");
}
