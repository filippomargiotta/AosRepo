using System.Globalization;
using System.Text.Json;
using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Aos.WebApi.Services;
using Microsoft.Extensions.Options;

namespace Aos.ReplayCli;

public static class RouterBenchmarkCliRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
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
            var config = await LoadRouterBenchmarkConfigAsync(request.ConfigPath, cancellationToken);
            var router = CreateRouter(config.RouterOptions, config.MetricsOptions);
            var report = RouterBenchmarkRunner.Run(
                router,
                request.SelectionRequest,
                new RouterBenchmarkOptions(request.Iterations, request.WarmupIterations));

            await stdout.WriteLineAsync($"Router benchmark: {report.TaskClass}");
            await stdout.WriteLineAsync($"metrics.enabled: {config.MetricsOptions.Enabled}");
            await stdout.WriteLineAsync($"metrics.blendWeight: {FormatDecimal(config.MetricsOptions.BlendWeight)}");
            await stdout.WriteLineAsync($"metrics.count: {config.MetricsOptions.Metrics.Count}");
            await stdout.WriteLineAsync($"iterations: {report.Iterations}");
            await stdout.WriteLineAsync($"warmupIterations: {report.WarmupIterations}");
            await stdout.WriteLineAsync(
                $"selected: {FormatSelectedModel(report.SelectedProvider, report.SelectedModelId, report.SelectedVersion)}");
            await stdout.WriteLineAsync($"selectedScore: {FormatNullableDecimal(report.SelectedScore)}");
            await stdout.WriteLineAsync($"rankedCandidates: {report.RankedCandidateCount}");
            await stdout.WriteLineAsync($"rejections: {report.RejectionCount}");
            await stdout.WriteLineAsync($"latency.minMs: {FormatLatency(report.MinLatencyMs)}");
            await stdout.WriteLineAsync($"latency.medianMs: {FormatLatency(report.MedianLatencyMs)}");
            await stdout.WriteLineAsync($"latency.p95Ms: {FormatLatency(report.P95LatencyMs)}");
            await stdout.WriteLineAsync($"latency.maxMs: {FormatLatency(report.MaxLatencyMs)}");

            if (request.CompareWithoutMetrics && config.MetricsOptions.Enabled)
            {
                var staticMetricsOptions = new RouterMetricsOptions
                {
                    Enabled = false,
                    BlendWeight = config.MetricsOptions.BlendWeight,
                    Metrics = config.MetricsOptions.Metrics
                };
                var staticReport = RouterBenchmarkRunner.Run(
                    CreateRouter(config.RouterOptions, staticMetricsOptions),
                    request.SelectionRequest,
                    new RouterBenchmarkOptions(request.Iterations, request.WarmupIterations));

                await stdout.WriteLineAsync("comparison.withoutMetrics:");
                await stdout.WriteLineAsync(
                    $"  selected: {FormatSelectedModel(staticReport.SelectedProvider, staticReport.SelectedModelId, staticReport.SelectedVersion)}");
                await stdout.WriteLineAsync($"  selectedScore: {FormatNullableDecimal(staticReport.SelectedScore)}");
                await stdout.WriteLineAsync($"  latency.p95Ms: {FormatLatency(staticReport.P95LatencyMs)}");
            }

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
            await stderr.WriteLineAsync($"Invalid router config JSON: {ex.Message}");
            return 2;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            await stderr.WriteLineAsync($"Router benchmark failed: {ex.Message}");
            return 1;
        }
    }

    private static bool TryParseArgs(string[] args, out RouterBenchmarkCliRequest request, out string error)
    {
        request = default;
        error = string.Empty;

        var configPath = "source/Aos.WebApi/appsettings.json";
        var taskClass = "workflow.hello";
        int? iterations = null;
        var warmupIterations = 1_000;
        int? maxLatencyMs = null;
        decimal? maxCostPer1KTokens = null;
        int? minQualityScore = null;
        var requiredComplianceTags = new List<string>();
        var compareWithoutMetrics = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config" when i + 1 < args.Length:
                    configPath = args[++i];
                    break;
                case "--iterations" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedIterations))
                    {
                        error = "The --iterations value must be an integer.";
                        return false;
                    }

                    iterations = parsedIterations;
                    break;
                case "--warmup" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out warmupIterations))
                    {
                        error = "The --warmup value must be an integer.";
                        return false;
                    }

                    break;
                case "--task-class" when i + 1 < args.Length:
                    taskClass = args[++i];
                    break;
                case "--max-latency-ms" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMaxLatency))
                    {
                        error = "The --max-latency-ms value must be an integer.";
                        return false;
                    }

                    maxLatencyMs = parsedMaxLatency;
                    break;
                case "--max-cost-per-1k-tokens" when i + 1 < args.Length:
                    if (!decimal.TryParse(args[++i], NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedMaxCost))
                    {
                        error = "The --max-cost-per-1k-tokens value must be a decimal.";
                        return false;
                    }

                    maxCostPer1KTokens = parsedMaxCost;
                    break;
                case "--min-quality-score" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMinQuality))
                    {
                        error = "The --min-quality-score value must be an integer.";
                        return false;
                    }

                    minQualityScore = parsedMinQuality;
                    break;
                case "--required-compliance-tag" when i + 1 < args.Length:
                    requiredComplianceTags.AddRange(
                        args[++i]
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--compare-without-metrics":
                    compareWithoutMetrics = true;
                    break;
                default:
                    error = $"Unknown or incomplete argument: {args[i]}";
                    return false;
            }
        }

        if (iterations is null)
        {
            error = "The --iterations argument is required.";
            return false;
        }

        request = new RouterBenchmarkCliRequest(
            configPath,
            iterations.Value,
            warmupIterations,
            compareWithoutMetrics,
            new RouterSelectionRequest(
                taskClass,
                maxLatencyMs,
                maxCostPer1KTokens,
                minQualityScore,
                requiredComplianceTags.Count == 0 ? null : requiredComplianceTags));
        return true;
    }

    private static async Task<RouterBenchmarkConfig> LoadRouterBenchmarkConfigAsync(
        string configPath,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(configPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty(RouterOptions.SectionName, out var routerSection))
        {
            throw new InvalidOperationException($"Config file '{configPath}' does not contain a Router section.");
        }

        var routerOptions = routerSection.Deserialize<RouterOptions>(JsonOptions)
            ?? throw new InvalidOperationException($"Config file '{configPath}' Router section deserialized to null.");
        var metricsOptions = document.RootElement.TryGetProperty(RouterMetricsOptions.SectionName, out var metricsSection)
            ? metricsSection.Deserialize<RouterMetricsOptions>(JsonOptions)
                ?? throw new InvalidOperationException($"Config file '{configPath}' RouterMetrics section deserialized to null.")
            : new RouterMetricsOptions();

        return new RouterBenchmarkConfig(routerOptions, metricsOptions);
    }

    private static DeterministicRouterService CreateRouter(
        RouterOptions routerOptions,
        RouterMetricsOptions metricsOptions)
    {
        var metricsStore = new InMemoryRouterMetricsStore(
            Options.Create(metricsOptions));
        return new DeterministicRouterService(
            Options.Create(routerOptions),
            Options.Create(metricsOptions),
            metricsStore);
    }

    private static string FormatSelectedModel(string? provider, string? modelId, string? version) =>
        string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(version)
            ? "(none)"
            : $"{provider}/{modelId}/{version}";

    private static string FormatLatency(double latencyMs) =>
        latencyMs.ToString("0.0000", CultureInfo.InvariantCulture);

    private static string FormatDecimal(decimal value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string FormatNullableDecimal(decimal? value) =>
        value is decimal score ? FormatDecimal(score) : "(none)";

    private static string GetUsageText() => """
        Usage: aos-replay benchmark-router --iterations <count> [--config <path>] [--warmup <count>] [--task-class <value>]
               [--max-latency-ms <value>] [--max-cost-per-1k-tokens <value>] [--min-quality-score <value>]
               [--required-compliance-tag <value>]... [--compare-without-metrics]

        Defaults: --config source/Aos.WebApi/appsettings.json --task-class workflow.hello --warmup 1000
        Exit codes: 0=reported, 1=benchmark/config failure, 2=usage/input failure
        """;

    private readonly record struct RouterBenchmarkCliRequest(
        string ConfigPath,
        int Iterations,
        int WarmupIterations,
        bool CompareWithoutMetrics,
        RouterSelectionRequest SelectionRequest
    );

    private sealed record RouterBenchmarkConfig(
        RouterOptions RouterOptions,
        RouterMetricsOptions MetricsOptions
    );
}
