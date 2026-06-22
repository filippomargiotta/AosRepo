using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Microsoft.Extensions.Options;

namespace Aos.WebApi.Services;

public sealed class HmacJwtCapabilityTokenService : ICapabilityTokenIssuer, ICapabilityTokenValidator
{
    public const string PolicyId = "capability-token-v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly CapabilityTokenOptions _options;
    private readonly byte[] _signingKey;

    public HmacJwtCapabilityTokenService(IOptions<CapabilityTokenOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        ValidateOptions(_options);
        _signingKey = Encoding.UTF8.GetBytes(_options.SigningKey);
    }

    public string Issue(ToolCapabilityScope scope, DateTimeOffset issuedAtUtc)
    {
        ValidateScope(scope);

        var issuedAt = issuedAtUtc.ToUnixTimeSeconds();
        var header = new JwtHeader("HS256", "JWT", _options.KeyId);
        var payload = new JwtPayload(
            Issuer: _options.Issuer,
            Audience: _options.Audience,
            TokenId: BuildTokenId(scope),
            IssuedAt: issuedAt,
            ExpiresAt: checked(issuedAt + _options.LifetimeSeconds),
            RunId: scope.RunId,
            InvocationId: scope.InvocationId,
            ToolId: scope.ToolId,
            ToolVersion: scope.ToolVersion,
            Action: scope.Action);

        var encodedHeader = EncodeJson(header);
        var encodedPayload = EncodeJson(payload);
        var signingInput = $"{encodedHeader}.{encodedPayload}";
        var signature = Sign(signingInput);
        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    public CapabilityDecision Validate(
        string capabilityToken,
        ToolCapabilityScope expectedScope,
        DateTimeOffset validationTimeUtc)
    {
        ValidateScope(expectedScope);

        if (string.IsNullOrWhiteSpace(capabilityToken))
        {
            return Deny("missing_token");
        }

        var segments = capabilityToken.Split('.');
        if (segments.Length != 3)
        {
            return Deny("malformed_token");
        }

        JwtHeader? header;
        JwtPayload? payload;
        byte[] providedSignature;
        try
        {
            header = DecodeJson<JwtHeader>(segments[0]);
            payload = DecodeJson<JwtPayload>(segments[1]);
            providedSignature = Base64UrlDecode(segments[2]);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return Deny("malformed_token");
        }

        if (header is null || payload is null)
        {
            return Deny("malformed_token");
        }

        if (!string.Equals(header.Algorithm, "HS256", StringComparison.Ordinal) ||
            !string.Equals(header.Type, "JWT", StringComparison.Ordinal) ||
            !string.Equals(header.KeyId, _options.KeyId, StringComparison.Ordinal))
        {
            return Deny("unsupported_token_header");
        }

        var expectedSignature = Sign($"{segments[0]}.{segments[1]}");
        if (providedSignature.Length != expectedSignature.Length ||
            !CryptographicOperations.FixedTimeEquals(providedSignature, expectedSignature))
        {
            return Deny("invalid_signature");
        }

        if (!string.Equals(payload.Issuer, _options.Issuer, StringComparison.Ordinal))
        {
            return Deny("issuer_mismatch", payload.TokenId);
        }

        if (!string.Equals(payload.Audience, _options.Audience, StringComparison.Ordinal))
        {
            return Deny("audience_mismatch", payload.TokenId);
        }

        var validationTime = validationTimeUtc.ToUnixTimeSeconds();
        if (payload.ExpiresAt <= validationTime)
        {
            return Deny("token_expired", payload.TokenId);
        }

        if (payload.IssuedAt > validationTime)
        {
            return Deny("token_not_yet_valid", payload.TokenId);
        }

        if (!string.Equals(payload.RunId, expectedScope.RunId, StringComparison.Ordinal))
        {
            return Deny("run_mismatch", payload.TokenId);
        }

        if (!string.Equals(payload.InvocationId, expectedScope.InvocationId, StringComparison.Ordinal))
        {
            return Deny("invocation_mismatch", payload.TokenId);
        }

        if (!string.Equals(payload.ToolId, expectedScope.ToolId, StringComparison.Ordinal))
        {
            return Deny("tool_mismatch", payload.TokenId);
        }

        if (!string.Equals(payload.ToolVersion, expectedScope.ToolVersion, StringComparison.Ordinal))
        {
            return Deny("tool_version_mismatch", payload.TokenId);
        }

        if (!string.Equals(payload.Action, expectedScope.Action, StringComparison.Ordinal))
        {
            return Deny("action_mismatch", payload.TokenId);
        }

        if (string.IsNullOrWhiteSpace(payload.TokenId))
        {
            return Deny("missing_token_id");
        }

        return new CapabilityDecision(PolicyId, "allow", "capability_valid", payload.TokenId);
    }

    private byte[] Sign(string signingInput) =>
        HMACSHA256.HashData(_signingKey, Encoding.ASCII.GetBytes(signingInput));

    private static string EncodeJson<T>(T value) =>
        Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));

    private static T? DecodeJson<T>(string encoded) =>
        JsonSerializer.Deserialize<T>(Base64UrlDecode(encoded), JsonOptions);

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = (base64.Length % 4) switch
        {
            0 => base64,
            2 => base64 + "==",
            3 => base64 + "=",
            _ => throw new FormatException("Invalid base64url value.")
        };
        return Convert.FromBase64String(base64);
    }

    private static string BuildTokenId(ToolCapabilityScope scope) =>
        $"cap:{scope.InvocationId}";

    private static CapabilityDecision Deny(string reasonCode, string? tokenId = null) =>
        new(PolicyId, "deny", reasonCode, tokenId);

    private static void ValidateOptions(CapabilityTokenOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Issuer) ||
            string.IsNullOrWhiteSpace(options.Audience) ||
            string.IsNullOrWhiteSpace(options.SigningKey) ||
            string.IsNullOrWhiteSpace(options.KeyId))
        {
            throw new InvalidOperationException(
                "Capability token issuer, audience, signing key, and key id are required.");
        }

        if (Encoding.UTF8.GetByteCount(options.SigningKey) < 32)
        {
            throw new InvalidOperationException("Capability token signing key must be at least 32 bytes.");
        }

        if (options.LifetimeSeconds <= 0)
        {
            throw new InvalidOperationException("Capability token lifetime must be greater than zero.");
        }
    }

    private static void ValidateScope(ToolCapabilityScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(scope.RunId) ||
            string.IsNullOrWhiteSpace(scope.InvocationId) ||
            string.IsNullOrWhiteSpace(scope.ToolId) ||
            string.IsNullOrWhiteSpace(scope.ToolVersion) ||
            string.IsNullOrWhiteSpace(scope.Action))
        {
            throw new ArgumentException("Capability scope fields are required.", nameof(scope));
        }
    }

    private sealed record JwtHeader(
        [property: JsonPropertyName("alg")] string Algorithm,
        [property: JsonPropertyName("typ")] string Type,
        [property: JsonPropertyName("kid")] string KeyId);

    private sealed record JwtPayload(
        [property: JsonPropertyName("iss")] string Issuer,
        [property: JsonPropertyName("aud")] string Audience,
        [property: JsonPropertyName("jti")] string TokenId,
        [property: JsonPropertyName("iat")] long IssuedAt,
        [property: JsonPropertyName("exp")] long ExpiresAt,
        [property: JsonPropertyName("run_id")] string RunId,
        [property: JsonPropertyName("invocation_id")] string InvocationId,
        [property: JsonPropertyName("tool_id")] string ToolId,
        [property: JsonPropertyName("tool_version")] string ToolVersion,
        [property: JsonPropertyName("action")] string Action);
}
