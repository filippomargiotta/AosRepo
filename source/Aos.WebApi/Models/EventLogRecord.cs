namespace Aos.WebApi.Models;

public sealed record EventLogRecord(
    string SchemaVersion,
    EventLogEntry Entry,
    EventLogIntegrity Integrity
);
