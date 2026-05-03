using System.Text;
using System.Text.Json;
using Aos.WebApi.Models;
using Aos.WebApi.Services;
using Xunit;

namespace Aos.WebApi.Tests;

internal static class GoldenArtifactTestSupport
{
    public const string HelloScenario = "hello-workflow-v1";
    public const string GoldenHmacKey = "golden-hmac-key";
    public const string GoldenHmacKeyId = "golden-key-1";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string GetGoldenRoot() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "Golden"));

    public static string GetGoldenDir(string scenario = HelloScenario) =>
        Path.Combine(GetGoldenRoot(), scenario);

    public static string GetGoldenPath(string fileName, string scenario = HelloScenario) =>
        Path.Combine(GetGoldenDir(scenario), fileName);

    public static string CreateTempDir(string scope)
    {
        var path = Path.Combine(Path.GetTempPath(), scope, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static string ReadGoldenManifestJson(string scenario = HelloScenario) =>
        File.ReadAllText(GetGoldenPath("manifest.json", scenario)).TrimEnd('\r', '\n');

    public static string ReadGoldenEventLogJsonl(string scenario = HelloScenario) =>
        File.ReadAllText(GetGoldenPath("eventlog.jsonl", scenario));

    public static ManifestRecord ReadGoldenManifestRecord(string scenario = HelloScenario)
    {
        var manifestRecord = JsonSerializer.Deserialize<ManifestRecord>(ReadGoldenManifestJson(scenario), JsonOptions);
        Assert.NotNull(manifestRecord);
        return manifestRecord!;
    }

    public static IReadOnlyList<EventLogRecord> ReadGoldenEventLogRecords(string scenario = HelloScenario)
    {
        var records = new List<EventLogRecord>();
        foreach (var line in ReadGoldenEventLogJsonl(scenario)
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var record = JsonSerializer.Deserialize<EventLogRecord>(line, JsonOptions);
            Assert.NotNull(record);
            records.Add(record!);
        }

        return records;
    }

    public static string SerializeManifestRecord(ManifestRecord manifestRecord) =>
        JsonSerializer.Serialize(manifestRecord, JsonOptions);

    public static string SerializeSignedManifest(
        Manifest manifest,
        string hmacKey = GoldenHmacKey,
        string keyId = GoldenHmacKeyId)
    {
        var record = new HmacManifestSigner(hmacKey, keyId).SignManifest(manifest);
        return JsonSerializer.Serialize(record, JsonOptions);
    }

    public static string SerializeEventLogLines(IEnumerable<EventLogRecord> records)
    {
        var builder = new StringBuilder();
        foreach (var record in records)
        {
            builder.Append(JsonSerializer.Serialize(record, JsonOptions));
            builder.Append('\n');
        }

        return builder.ToString();
    }

    public static string SerializeSignedEventLogLines(
        IReadOnlyList<EventLogEntry> entries,
        string hmacKey = GoldenHmacKey,
        string keyId = GoldenHmacKeyId)
    {
        var records = new HmacEventLogIntegrityChain(hmacKey, keyId).SignEntries(entries);
        return string.Join('\n', records.Select(record => JsonSerializer.Serialize(record, JsonOptions)));
    }
}
