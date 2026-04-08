using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public sealed class HmacManifestSigner : IManifestSigner
{
    private const string Algorithm = "HMAC-SHA256";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly byte[] _hmacKeyBytes;
    private readonly string _keyId;

    public HmacManifestSigner(string hmacKey, string keyId)
    {
        if (string.IsNullOrWhiteSpace(hmacKey))
        {
            throw new ArgumentException("Manifest HMAC key is required.", nameof(hmacKey));
        }

        if (string.IsNullOrWhiteSpace(keyId))
        {
            throw new ArgumentException("Manifest HMAC key id is required.", nameof(keyId));
        }

        _hmacKeyBytes = Encoding.UTF8.GetBytes(hmacKey);
        _keyId = keyId;
    }

    public ManifestRecord SignManifest(Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return new ManifestRecord(
            Manifest: manifest,
            Integrity: new ManifestIntegrity(
                Algorithm: Algorithm,
                KeyId: _keyId,
                ManifestMac: ComputeManifestMac(manifest)));
    }

    public bool TryValidateRecord(ManifestRecord record, out string? error)
    {
        ArgumentNullException.ThrowIfNull(record);

        error = null;

        if (!string.Equals(record.Integrity.Algorithm, Algorithm, StringComparison.Ordinal))
        {
            error = $"Manifest integrity algorithm '{record.Integrity.Algorithm}' is not supported.";
            return false;
        }

        if (!string.Equals(record.Integrity.KeyId, _keyId, StringComparison.Ordinal))
        {
            error = $"Manifest integrity keyId '{record.Integrity.KeyId}' does not match expected key id '{_keyId}'.";
            return false;
        }

        var expectedManifestMac = ComputeManifestMac(record.Manifest);
        if (!string.Equals(record.Integrity.ManifestMac, expectedManifestMac, StringComparison.Ordinal))
        {
            error = "Manifest integrity MAC is invalid.";
            return false;
        }

        return true;
    }

    private string ComputeManifestMac(Manifest manifest)
    {
        var payload = $"{manifest.ManifestVersion}\n{_keyId}\n{JsonSerializer.Serialize(manifest, JsonOptions)}";
        var mac = HMACSHA256.HashData(_hmacKeyBytes, Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }
}
