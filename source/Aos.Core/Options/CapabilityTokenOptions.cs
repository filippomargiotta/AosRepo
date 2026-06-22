namespace Aos.WebApi.Options;

public sealed class CapabilityTokenOptions
{
    public const string SectionName = "CapabilityTokens";

    public string Issuer { get; set; } = "aos.local";
    public string Audience { get; set; } = "aos.tools";
    public string SigningKey { get; set; } = string.Empty;
    public string KeyId { get; set; } = "local-capability-key";
    public int LifetimeSeconds { get; set; } = 300;
}
