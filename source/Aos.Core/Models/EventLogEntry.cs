namespace Aos.WebApi.Models;

public sealed record EventLogEntry(
    string RunId,
    string EventType,
    object? Data,
    DateTimeOffset OccurredAtUtc
);
