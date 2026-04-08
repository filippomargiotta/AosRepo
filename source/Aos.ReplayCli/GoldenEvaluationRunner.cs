using System.Text.Json;

namespace Aos.ReplayCli;

public static class GoldenEvaluationRunner
{
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

        var scenarioFiles = Directory.EnumerateFiles(request.ScenariosRootPath, "scenario.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (scenarioFiles.Length == 0)
        {
            await stderr.WriteLineAsync($"No scenario.json files were found under '{request.ScenariosRootPath}'.");
            return 2;
        }

        var passed = 0;
        var failed = 0;

        foreach (var scenarioFile in scenarioFiles)
        {
            var scenario = await LoadScenarioAsync(scenarioFile, cancellationToken);
            var scenarioDirectory = Path.GetDirectoryName(scenarioFile)
                ?? throw new InvalidOperationException($"Could not determine scenario directory for '{scenarioFile}'.");

            using var replayStdout = new StringWriter();
            using var replayStderr = new StringWriter();

            var exitCode = await ReplayCliRunner.RunAsync(
                [
                    "--workflow", scenario.Workflow,
                    "--manifest", Path.Combine(scenarioDirectory, scenario.ManifestPath),
                    "--eventlog", Path.Combine(scenarioDirectory, scenario.EventLogPath),
                    "--hmac-key", request.HmacKey
                ],
                replayStdout,
                replayStderr,
                cancellationToken);

            if (exitCode == 0)
            {
                passed++;
                await stdout.WriteLineAsync($"PASS {scenario.Name}");
                continue;
            }

            failed++;
            await stdout.WriteLineAsync($"FAIL {scenario.Name}");
            var replayError = replayStderr.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(replayError))
            {
                await stderr.WriteLineAsync($"[{scenario.Name}] {replayError}");
            }
        }

        await stdout.WriteLineAsync($"Evaluated {scenarioFiles.Length} scenario(s): {passed} passed, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    private static bool TryParseArgs(string[] args, out EvaluationRequest request, out string error)
    {
        request = default;
        error = string.Empty;

        string? scenariosRootPath = null;
        string? hmacKey = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--scenarios-root" when i + 1 < args.Length:
                    scenariosRootPath = args[++i];
                    break;
                case "--hmac-key" when i + 1 < args.Length:
                    hmacKey = args[++i];
                    break;
                default:
                    error = $"Unknown or incomplete argument: {args[i]}";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(scenariosRootPath) || string.IsNullOrWhiteSpace(hmacKey))
        {
            error = "The --scenarios-root and --hmac-key arguments are required.";
            return false;
        }

        request = new EvaluationRequest(scenariosRootPath, hmacKey);
        return true;
    }

    private static async Task<EvaluationScenario> LoadScenarioAsync(string path, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var scenario = JsonSerializer.Deserialize<EvaluationScenario>(json, JsonOptions);
        if (scenario is null)
        {
            throw new JsonException($"Scenario JSON at '{path}' deserialized to null.");
        }

        var name = string.IsNullOrWhiteSpace(scenario.Name)
            ? Path.GetFileName(Path.GetDirectoryName(path))
            : scenario.Name;

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException($"Scenario name is required in '{path}'.");
        }

        if (string.IsNullOrWhiteSpace(scenario.Workflow))
        {
            throw new InvalidOperationException($"Scenario workflow is required in '{path}'.");
        }

        if (string.IsNullOrWhiteSpace(scenario.ManifestPath))
        {
            throw new InvalidOperationException($"Scenario manifestPath is required in '{path}'.");
        }

        if (string.IsNullOrWhiteSpace(scenario.EventLogPath))
        {
            throw new InvalidOperationException($"Scenario eventLogPath is required in '{path}'.");
        }

        return scenario with { Name = name };
    }

    private static string GetUsageText()
    {
        return "Usage: aos-replay evaluate --scenarios-root <path> --hmac-key <value>";
    }

    private readonly record struct EvaluationRequest(string ScenariosRootPath, string HmacKey);

    private sealed record EvaluationScenario(
        string? Name,
        string Workflow,
        string ManifestPath,
        string EventLogPath
    );
}
