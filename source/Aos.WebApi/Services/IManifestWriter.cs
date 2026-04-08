using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public interface IManifestWriter
{
    Task WriteAsync(ManifestRecord record, CancellationToken cancellationToken = default);
}
