using System.Text;
using System.Text.Json;
using Aos.WebApi.Models;
using Aos.WebApi.Services;

namespace Aos.ReplayCli;

public static class ReplayCliRunner
{
    private static readonly IReadOnlyDictionary<string, IReplayWorkflow> Workflows =
        new IReplayWorkflow[] { new HelloReplayWorkflow(), new PlannerReplayWorkflow() }
            .ToDictionary(workflow => workflow.WorkflowName, StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (!TryParseArgs(args, out var request, out var parseError))
        {
            await stderr.WriteLineAsync(parseError);
            await stderr.WriteLineAsync(GetUsageText());
            return 2;
        }

        try
        {
            var manifestRecord = await LoadManifestRecordAsync(request.ManifestPath, cancellationToken);
            var expectedRecords = await LoadEventLogRecordsAsync(request.EventLogPath, cancellationToken);
            var manifest = manifestRecord.Manifest;

            var manifestErrors = ManifestValidator.Validate(manifest);
            if (manifestErrors.Count > 0)
            {
                await stderr.WriteLineAsync($"Manifest validation failed: {string.Join(" ", manifestErrors)}");
                return 1;
            }

            var manifestSigner = new HmacManifestSigner(
                request.HmacKey,
                manifestRecord.Integrity.KeyId);

            if (!manifestSigner.TryValidateRecord(manifestRecord, out var manifestIntegrityError))
            {
                await stderr.WriteLineAsync($"Artifact integrity failed: {manifestIntegrityError}");
                return 1;
            }

            if (expectedRecords.Count == 0)
            {
                await stderr.WriteLineAsync("Event log is empty.");
                return 1;
            }

            var eventLogIntegrityChain = new HmacEventLogIntegrityChain(
                request.HmacKey,
                expectedRecords[0].Integrity.KeyId);

            if (!eventLogIntegrityChain.TryValidateRecords(expectedRecords, out var integrityError))
            {
                await stderr.WriteLineAsync($"Artifact integrity failed: {integrityError}");
                return 1;
            }

            var compatibilityErrors = GetArtifactCompatibilityErrors(manifest, expectedRecords);
            if (compatibilityErrors.Count > 0)
            {
                foreach (var compatibilityError in compatibilityErrors)
                {
                    await stderr.WriteLineAsync($"Artifact compatibility failed: {compatibilityError}");
                }

                return 1;
            }

            if (!Workflows.TryGetValue(request.WorkflowName, out var workflow))
            {
                await stderr.WriteLineAsync(
                    $"Unknown workflow '{request.WorkflowName}'. Available workflows: {string.Join(", ", GetWorkflowNames())}.");
                return 2;
            }

            var actual = workflow.Replay(manifest, expectedRecords, eventLogIntegrityChain, manifestSigner);
            var mismatches = GetManifestRecordMismatches(manifestRecord, actual.ManifestRecord);
            var eventLogMismatches = GetEventLogMismatches(
                expectedRecords.Select(record => record.Entry).ToArray(),
                actual.EventLogRecords.Select(record => record.Entry).ToArray());

            var expectedEventLogJson = SerializeEventLogLines(expectedRecords);
            var actualEventLogJson = SerializeEventLogLines(actual.EventLogRecords);
            if (eventLogMismatches.Count == 0 &&
                !string.Equals(expectedEventLogJson, actualEventLogJson, StringComparison.Ordinal))
            {
                eventLogMismatches.Add("Event log bytes differ from replay output.");
            }

            mismatches.AddRange(eventLogMismatches);

            if (mismatches.Count > 0)
            {
                foreach (var mismatch in mismatches)
                {
                    await stderr.WriteLineAsync($"Mismatch: {mismatch}");
                }

                return 1;
            }

            await stdout.WriteLineAsync($"Replay verified for run {manifest.RunId}.");
            return 0;
        }
        catch (FileNotFoundException ex)
        {
            await stderr.WriteLineAsync(ex.Message);
            return 2;
        }
        catch (DirectoryNotFoundException ex)
        {
            await stderr.WriteLineAsync(ex.Message);
            return 2;
        }
        catch (JsonException ex)
        {
            await stderr.WriteLineAsync($"Invalid JSON input: {ex.Message}");
            return 2;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            await stderr.WriteLineAsync($"Replay failed: {ex.Message}");
            return 1;
        }
    }

    private static bool TryParseArgs(string[] args, out ReplayRequest request, out string error)
    {
        request = default;
        error = string.Empty;

        if (args.Length == 0)
        {
            error = "Missing required arguments.";
            return false;
        }

        string? workflowName = null;
        string? artifactDir = null;
        string? manifestPath = null;
        string? eventLogPath = null;
        string? hmacKey = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--workflow" when i + 1 < args.Length:
                    workflowName = args[++i];
                    break;
                case "--artifact-dir" when i + 1 < args.Length:
                    artifactDir = args[++i];
                    break;
                case "--manifest" when i + 1 < args.Length:
                    manifestPath = args[++i];
                    break;
                case "--eventlog" when i + 1 < args.Length:
                    eventLogPath = args[++i];
                    break;
                case "--hmac-key" when i + 1 < args.Length:
                    hmacKey = args[++i];
                    break;
                default:
                    error = $"Unknown or incomplete argument: {args[i]}";
                    return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(artifactDir))
        {
            if (!string.IsNullOrWhiteSpace(manifestPath) || !string.IsNullOrWhiteSpace(eventLogPath))
            {
                error = "Use either --artifact-dir or the explicit --manifest/--eventlog paths, not both.";
                return false;
            }

            manifestPath = Path.Combine(artifactDir, "manifest.json");
            eventLogPath = Path.Combine(artifactDir, "eventlog.jsonl");
        }

        if (string.IsNullOrWhiteSpace(workflowName) ||
            string.IsNullOrWhiteSpace(manifestPath) ||
            string.IsNullOrWhiteSpace(eventLogPath) ||
            string.IsNullOrWhiteSpace(hmacKey))
        {
            error = "The --workflow, --hmac-key, and either --artifact-dir or both --manifest/--eventlog arguments are required.";
            return false;
        }

        request = new ReplayRequest(workflowName, manifestPath, eventLogPath, hmacKey);
        return true;
    }

    private static async Task<ManifestRecord> LoadManifestRecordAsync(string path, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var manifestRecord = JsonSerializer.Deserialize<ManifestRecord>(json, JsonOptions);
        if (manifestRecord is null)
        {
            throw new JsonException("Manifest JSON deserialized to null.");
        }

        return manifestRecord;
    }

    private static async Task<IReadOnlyList<EventLogRecord>> LoadEventLogRecordsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        var records = new List<EventLogRecord>();

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var record = JsonSerializer.Deserialize<EventLogRecord>(line, JsonOptions);
            if (record is null)
            {
                throw new JsonException("Event log line deserialized to null.");
            }

            records.Add(record);
        }

        return records;
    }

    private static List<string> GetManifestRecordMismatches(ManifestRecord expected, ManifestRecord actual)
    {
        var differences = new List<JsonDifference>();
        AddJsonDifferences(
            JsonSerializer.SerializeToElement(expected, JsonOptions),
            JsonSerializer.SerializeToElement(actual, JsonOptions),
            path: string.Empty,
            differences);

        return differences
            .Select(difference => $"Manifest field {FormatFieldPath(difference.Path)} differs: {difference.Message}.")
            .ToList();
    }

    private static List<string> GetEventLogMismatches(
        IReadOnlyList<EventLogEntry> expectedEntries,
        IReadOnlyList<EventLogEntry> actualEntries)
    {
        var mismatches = new List<string>();
        var sharedCount = Math.Min(expectedEntries.Count, actualEntries.Count);

        if (expectedEntries.Count != actualEntries.Count)
        {
            mismatches.Add($"Event log line count differs: expected {expectedEntries.Count}, actual {actualEntries.Count}.");
        }

        for (var i = 0; i < sharedCount; i++)
        {
            var differences = new List<JsonDifference>();
            AddJsonDifferences(
                JsonSerializer.SerializeToElement(expectedEntries[i], JsonOptions),
                JsonSerializer.SerializeToElement(actualEntries[i], JsonOptions),
                path: string.Empty,
                differences);

            mismatches.AddRange(
                differences.Select(difference =>
                    $"Event log line {i + 1} field {FormatFieldPath(difference.Path)} differs: {difference.Message}."));
        }

        for (var i = sharedCount; i < expectedEntries.Count; i++)
        {
            mismatches.Add($"Event log line {i + 1} is missing from replay output.");
        }

        for (var i = sharedCount; i < actualEntries.Count; i++)
        {
            mismatches.Add($"Event log line {i + 1} is unexpected in replay output.");
        }

        return mismatches;
    }

    private static List<string> GetArtifactCompatibilityErrors(
        Manifest manifest,
        IReadOnlyList<EventLogRecord> eventLogRecords)
    {
        var errors = new List<string>();

        if (!string.Equals(manifest.EventLog.SchemaVersion, eventLogRecords[0].SchemaVersion, StringComparison.Ordinal))
        {
            errors.Add(
                $"Manifest eventLog schemaVersion '{manifest.EventLog.SchemaVersion}' does not match event log schemaVersion '{eventLogRecords[0].SchemaVersion}'.");
        }

        if (manifest.EventLog.RecordCount != eventLogRecords.Count)
        {
            errors.Add(
                $"Manifest eventLog recordCount '{manifest.EventLog.RecordCount}' does not match event log line count '{eventLogRecords.Count}'.");
        }

        var actualLastChainMac = eventLogRecords[^1].Integrity.ChainMac;
        if (!string.Equals(manifest.EventLog.LastChainMac, actualLastChainMac, StringComparison.Ordinal))
        {
            errors.Add(
                $"Manifest eventLog lastChainMac '{manifest.EventLog.LastChainMac}' does not match event log tail chainMac '{actualLastChainMac}'.");
        }

        for (var i = 0; i < eventLogRecords.Count; i++)
        {
            var entry = eventLogRecords[i].Entry;

            if (!string.Equals(entry.RunId, manifest.RunId, StringComparison.Ordinal))
            {
                errors.Add(
                    $"Event log line {i + 1} runId '{entry.RunId}' does not match manifest runId '{manifest.RunId}'.");
            }

            if (!TryGetPayloadManifestVersion(entry, out var payloadManifestVersion))
            {
                continue;
            }

            if (!string.Equals(payloadManifestVersion, manifest.ManifestVersion, StringComparison.Ordinal))
            {
                errors.Add(
                    $"Event log line {i + 1} payload manifestVersion '{payloadManifestVersion}' does not match manifest version '{manifest.ManifestVersion}'.");
            }
        }

        return errors;
    }

    private static bool TryGetPayloadManifestVersion(EventLogEntry entry, out string? manifestVersion)
    {
        manifestVersion = null;
        if (entry.Data is null)
        {
            return false;
        }

        var data = JsonSerializer.SerializeToElement(entry.Data, JsonOptions);
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("manifestVersion", out var manifestVersionElement) ||
            manifestVersionElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        manifestVersion = manifestVersionElement.GetString();
        return !string.IsNullOrWhiteSpace(manifestVersion);
    }

    private static void AddJsonDifferences(
        JsonElement expected,
        JsonElement actual,
        string path,
        ICollection<JsonDifference> differences)
    {
        if (expected.ValueKind != actual.ValueKind)
        {
            differences.Add(new JsonDifference(
                path,
                $"expected kind {expected.ValueKind}, actual kind {actual.ValueKind}"));
            return;
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                CompareObjectProperties(expected, actual, path, differences);
                break;
            case JsonValueKind.Array:
                CompareArrayItems(expected, actual, path, differences);
                break;
            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                if (!string.Equals(expected.GetRawText(), actual.GetRawText(), StringComparison.Ordinal))
                {
                    differences.Add(new JsonDifference(
                        path,
                        $"expected {expected.GetRawText()}, actual {actual.GetRawText()}"));
                }

                break;
        }
    }

    private static void CompareObjectProperties(
        JsonElement expected,
        JsonElement actual,
        string path,
        ICollection<JsonDifference> differences)
    {
        var expectedProperties = expected.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
        var actualProperties = actual.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);

        foreach (var propertyName in expectedProperties.Keys.Union(actualProperties.Keys).OrderBy(name => name, StringComparer.Ordinal))
        {
            var childPath = JoinPath(path, propertyName);
            var hasExpected = expectedProperties.TryGetValue(propertyName, out var expectedValue);
            var hasActual = actualProperties.TryGetValue(propertyName, out var actualValue);

            if (!hasExpected)
            {
                differences.Add(new JsonDifference(childPath, "unexpected in replay output"));
                continue;
            }

            if (!hasActual)
            {
                differences.Add(new JsonDifference(childPath, "missing from replay output"));
                continue;
            }

            AddJsonDifferences(expectedValue, actualValue, childPath, differences);
        }
    }

    private static void CompareArrayItems(
        JsonElement expected,
        JsonElement actual,
        string path,
        ICollection<JsonDifference> differences)
    {
        var expectedItems = expected.EnumerateArray().ToArray();
        var actualItems = actual.EnumerateArray().ToArray();
        var sharedCount = Math.Min(expectedItems.Length, actualItems.Length);

        if (expectedItems.Length != actualItems.Length)
        {
            differences.Add(new JsonDifference(
                path,
                $"expected {expectedItems.Length} item(s), actual {actualItems.Length}"));
        }

        for (var i = 0; i < sharedCount; i++)
        {
            AddJsonDifferences(expectedItems[i], actualItems[i], JoinPath(path, $"[{i}]"), differences);
        }

        for (var i = sharedCount; i < expectedItems.Length; i++)
        {
            differences.Add(new JsonDifference(JoinPath(path, $"[{i}]"), "missing from replay output"));
        }

        for (var i = sharedCount; i < actualItems.Length; i++)
        {
            differences.Add(new JsonDifference(JoinPath(path, $"[{i}]"), "unexpected in replay output"));
        }
    }

    private static string JoinPath(string path, string segment)
    {
        if (path.Length == 0)
        {
            return segment;
        }

        return segment.StartsWith("[", StringComparison.Ordinal)
            ? $"{path}{segment}"
            : $"{path}.{segment}";
    }

    private static string FormatFieldPath(string path)
    {
        return string.IsNullOrWhiteSpace(path) ? "<root>" : path;
    }

    private static string SerializeEventLogLines(IEnumerable<EventLogRecord> records)
    {
        var builder = new StringBuilder();
        foreach (var record in records)
        {
            builder.Append(JsonSerializer.Serialize(record, JsonOptions));
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string GetUsageText()
    {
        return
            $"Usage: aos-replay --workflow <name> --manifest <path> --eventlog <path> --hmac-key <value>{Environment.NewLine}" +
            $"   or: aos-replay --workflow <name> --artifact-dir <path> --hmac-key <value>{Environment.NewLine}" +
            $"Exit codes: 0=verified, 1=integrity/compatibility/mismatch failure, 2=usage/input failure{Environment.NewLine}" +
            $"Available workflows: {string.Join(", ", GetWorkflowNames())}";
    }

    private static IEnumerable<string> GetWorkflowNames() => Workflows.Keys.OrderBy(name => name, StringComparer.Ordinal);

    private sealed record JsonDifference(string Path, string Message);

    private readonly record struct ReplayRequest(string WorkflowName, string ManifestPath, string EventLogPath, string HmacKey);
}
