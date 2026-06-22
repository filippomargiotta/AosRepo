using Aos.WebApi.Options;
using Aos.WebApi.Services;

namespace Aos.WebApi.Tests;

internal static class CapabilityTestData
{
    private const string TestSigningKey = "test-capability-key-at-least-32-bytes";

    public static HmacJwtCapabilityTokenService CreateTokenService(
        int lifetimeSeconds = 300,
        string signingKey = TestSigningKey) =>
        new(Microsoft.Extensions.Options.Options.Create(new CapabilityTokenOptions
        {
            Issuer = "aos.tests",
            Audience = "aos.tools",
            SigningKey = signingKey,
            KeyId = "test-capability-v1",
            LifetimeSeconds = lifetimeSeconds
        }));

    public static IToolExecutor CreateEnforcingExecutor(
        HmacJwtCapabilityTokenService tokenService,
        IToolExecutor? innerExecutor = null) =>
        new CapabilityEnforcingToolExecutor(
            tokenService,
            innerExecutor ?? new DeterministicEchoToolExecutor());
}
