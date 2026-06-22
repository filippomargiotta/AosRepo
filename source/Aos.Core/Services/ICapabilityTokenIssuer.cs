using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public interface ICapabilityTokenIssuer
{
    string Issue(ToolCapabilityScope scope, DateTimeOffset issuedAtUtc);
}
