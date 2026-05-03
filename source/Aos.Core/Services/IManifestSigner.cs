using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public interface IManifestSigner
{
    ManifestRecord SignManifest(Manifest manifest);

    bool TryValidateRecord(ManifestRecord record, out string? error);
}
