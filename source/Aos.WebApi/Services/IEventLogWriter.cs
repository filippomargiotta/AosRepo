using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public interface IEventLogWriter
{
    Task WriteAsync(EventLogRecord record, CancellationToken cancellationToken = default);
}
