using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public interface IEventLogIntegrityChain
{
    IReadOnlyList<EventLogRecord> SignEntries(IReadOnlyList<EventLogEntry> entries);

    bool TryValidateRecords(IReadOnlyList<EventLogRecord> records, out string? error);
}
