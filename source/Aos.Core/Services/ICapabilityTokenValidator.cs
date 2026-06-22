using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public interface ICapabilityTokenValidator
{
    CapabilityDecision Validate(
        string capabilityToken,
        ToolCapabilityScope expectedScope,
        DateTimeOffset validationTimeUtc);
}
