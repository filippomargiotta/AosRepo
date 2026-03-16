using System.Text;
using System.Text.Json;
using Aos.WebApi.Models;

namespace Aos.ReplayCli;

public static class ReplayCliRunner
{
    private const string IgnoredManifestField = "timeSource";
    private static readonly IReadOnlyDictionary<string, IReplayWorkflow> Workflows =
        new IReplayWorkflow[] { new HelloReplayWorkflow() }
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
            var manifest = await LoadManifestAsync(request.ManifestPath, cancellationToken);
            var expectedEntries = await LoadEventLogEntriesAsync(request.EventLogPath, cancellationToken);

            var manifestErrors = ManifestValidator.Validate(manifest);
            if (manifestErrors.Count > 0)
            {
                await stderr.WriteLineAsync($"Manifest validation failed: {string.Join(" ", manifestErrors)}");
                return 1;
            }

            if (expectedEntries.Count == 0)
            {
                await stderr.WriteLineAsync("Event log is empty.");
                return 1;
            }

            var compatibilityErrors = GetArtifactCompatibilityErrors(manifest, expectedEntries);
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

            var actual = workflow.Replay(manifest, expectedEntries);
            var mismatches = GetDeterministicManifestMismatches(manifest, actual.Manifest);
            var eventLogMismatches = GetEventLogMismatches(expectedEntries, actual.EventLogEntries);

            var expectedEventLogJson = SerializeEventLogLines(expectedEntries);
            var actualEventLogJson = SerializeEventLogLines(actual.EventLogEntries);
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
        string? manifestPath = null;
        string? eventLogPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--workflow" when i + 1 < args.Length:
                    workflowName = args[++i];
                    break;
                case "--manifest" when i + 1 < args.Length:
                    manifestPath = args[++i];
                    break;
                case "--eventlog" when i + 1 < args.Length:
                    eventLogPath = args[++i];
                    break;
                default:
                    error = $"Unknown or incomplete argument: {args[i]}";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(workflowName) ||
            string.IsNullOrWhiteSpace(manifestPath) ||
            string.IsNullOrWhiteSpace(eventLogPath))
        {
            error = "The --workflow, --manifest, and --eventlog arguments are required.";
            return false;
        }

        request = new ReplayRequest(workflowName, manifestPath, eventLogPath);
        return true;
    }

    private static async Task<Manifest> LoadManifestAsync(string path, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var manifest = JsonSerializer.Deserialize<Manifest>(json, JsonOptions);
        if (manifest is null)
        {
            throw new JsonException("Manifest JSON deserialized to null.");
        }

        return manifest;
    }

    private static async Task<IReadOnlyList<EventLogEntry>> LoadEventLogEntriesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        var entries = new List<EventLogEntry>();

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = JsonSerializer.Deserialize<EventLogEntry>(line, JsonOptions);
            if (entry is null)
            {
                throw new JsonException("Event log line deserialized to null.");
            }

            entries.Add(entry);
        }

        return entries;
    }

    private static List<string> GetDeterministicManifestMismatches(Manifest expected, Manifest actual)
    {
        var differences = new List<JsonDifference>();
        AddJsonDifferences(
            JsonSerializer.SerializeToElement(expected, JsonOptions),
            JsonSerializer.SerializeToElement(actual, JsonOptions),
            path: string.Empty,
            differences,
            new HashSet<string>(StringComparer.Ordinal) { IgnoredManifestField });

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
        IReadOnlyList<EventLogEntry> eventLogEntries)
    {
        var errors = new List<string>();

        for (var i = 0; i < eventLogEntries.Count; i++)
        {
            var entry = eventLogEntries[i];

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
        ICollection<JsonDifference> differences,
        IReadOnlySet<string>? ignoredRootProperties = null)
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
                CompareObjectProperties(expected, actual, path, differences, ignoredRootProperties);
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
        ICollection<JsonDifference> differences,
        IReadOnlySet<string>? ignoredRootProperties)
    {
        var expectedProperties = expected.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
        var actualProperties = actual.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);

        foreach (var propertyName in expectedProperties.Keys.Union(actualProperties.Keys).OrderBy(name => name, StringComparer.Ordinal))
        {
            if (path.Length == 0 && ignoredRootProperties?.Contains(propertyName) == true)
            {
                continue;
            }

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

    private static string SerializeEventLogLines(IEnumerable<EventLogEntry> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            builder.Append(JsonSerializer.Serialize(entry, JsonOptions));
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string GetUsageText()
    {
        return $"Usage: aos-replay --workflow <name> --manifest <path> --eventlog <path>{Environment.NewLine}Available workflows: {string.Join(", ", GetWorkflowNames())}";
    }

    private static IEnumerable<string> GetWorkflowNames() => Workflows.Keys.OrderBy(name => name, StringComparer.Ordinal);

    private sealed record JsonDifference(string Path, string Message);

    private readonly record struct ReplayRequest(string WorkflowName, string ManifestPath, string EventLogPath);
}
