namespace Aos.WebApi.Models;

public sealed record EventLogIntegrity(
    string Algorithm,
    string KeyId,
    string? PreviousChainMac,
    string ChainMac
);
