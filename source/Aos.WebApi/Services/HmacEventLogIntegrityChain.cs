using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public sealed class HmacEventLogIntegrityChain : IEventLogIntegrityChain
{
    private const string Algorithm = "HMAC-SHA256";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly byte[] _hmacKeyBytes;
    private readonly string _keyId;

    public HmacEventLogIntegrityChain(string hmacKey, string keyId)
    {
        if (string.IsNullOrWhiteSpace(hmacKey))
        {
            throw new ArgumentException("Event log HMAC key is required.", nameof(hmacKey));
        }

        if (string.IsNullOrWhiteSpace(keyId))
        {
            throw new ArgumentException("Event log HMAC key id is required.", nameof(keyId));
        }

        _hmacKeyBytes = Encoding.UTF8.GetBytes(hmacKey);
        _keyId = keyId;
    }

    public IReadOnlyList<EventLogRecord> SignEntries(IReadOnlyList<EventLogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var records = new List<EventLogRecord>(entries.Count);
        string? previousChainMac = null;

        foreach (var entry in entries)
        {
            var chainMac = ComputeChainMac(SchemaVersions.CurrentEventLogSchemaVersion, entry, previousChainMac);
            records.Add(new EventLogRecord(
                SchemaVersion: SchemaVersions.CurrentEventLogSchemaVersion,
                Entry: entry,
                Integrity: new EventLogIntegrity(
                    Algorithm: Algorithm,
                    KeyId: _keyId,
                    PreviousChainMac: previousChainMac,
                    ChainMac: chainMac)));

            previousChainMac = chainMac;
        }

        return records;
    }

    public bool TryValidateRecords(IReadOnlyList<EventLogRecord> records, out string? error)
    {
        ArgumentNullException.ThrowIfNull(records);

        error = null;
        string? previousChainMac = null;

        for (var i = 0; i < records.Count; i++)
        {
            var lineNumber = i + 1;
            var record = records[i];

            if (!SchemaVersions.IsSupportedEventLogSchemaVersion(record.SchemaVersion))
            {
                error = $"Event log line {lineNumber} schemaVersion '{record.SchemaVersion}' is not supported.";
                return false;
            }

            if (!string.Equals(record.Integrity.Algorithm, Algorithm, StringComparison.Ordinal))
            {
                error = $"Event log line {lineNumber} integrity algorithm '{record.Integrity.Algorithm}' is not supported.";
                return false;
            }

            if (!string.Equals(record.Integrity.KeyId, _keyId, StringComparison.Ordinal))
            {
                error = $"Event log line {lineNumber} integrity keyId '{record.Integrity.KeyId}' does not match expected key id '{_keyId}'.";
                return false;
            }

            if (!string.Equals(record.Integrity.PreviousChainMac, previousChainMac, StringComparison.Ordinal))
            {
                error = $"Event log line {lineNumber} previousChainMac does not match the prior chain state.";
                return false;
            }

            var expectedChainMac = ComputeChainMac(record.SchemaVersion, record.Entry, previousChainMac);
            if (!string.Equals(record.Integrity.ChainMac, expectedChainMac, StringComparison.Ordinal))
            {
                error = $"Event log line {lineNumber} chainMac is invalid.";
                return false;
            }

            previousChainMac = record.Integrity.ChainMac;
        }

        return true;
    }

    private string ComputeChainMac(string schemaVersion, EventLogEntry entry, string? previousChainMac)
    {
        var payload = $"{schemaVersion}\n{_keyId}\n{previousChainMac ?? string.Empty}\n{JsonSerializer.Serialize(entry, JsonOptions)}";
        var mac = HMACSHA256.HashData(_hmacKeyBytes, Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }
}
