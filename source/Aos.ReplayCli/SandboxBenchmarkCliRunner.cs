using System.Globalization;
using Aos.WebApi.Options;
using Aos.WebApi.Services;

namespace Aos.ReplayCli;

public static class SandboxBenchmarkCliRunner
{
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
            var poolOptions = new SandboxPoolOptions
            {
                PoolSize = request.PoolSize,
                ExecutorType = request.ExecutorType,
                ContainerImage = request.ContainerImage
            };
            using var pool = new PreWarmedSandboxPool(
                request.PoolSize,
                SandboxSlotFactory.Create(poolOptions));
            using var executor = new PooledSandboxToolExecutor(pool, Microsoft.Extensions.Options.Options.Create(poolOptions));
            var report = SandboxBenchmarkRunner.Run(
                executor,
                request.PoolSize,
                poolOptions.ExecutorType,
                new SandboxBenchmarkOptions(request.Iterations, request.WarmupIterations));

            await stdout.WriteLineAsync($"Sandbox benchmark: {poolOptions.ExecutorType}");
            await stdout.WriteLineAsync($"executor.type: {report.ExecutorType}");
            await stdout.WriteLineAsync($"pool.size: {report.PoolSize}");
            await stdout.WriteLineAsync($"iterations: {report.Iterations}");
            await stdout.WriteLineAsync($"warmupIterations: {report.WarmupIterations}");
            await stdout.WriteLineAsync($"warmStart.count: {report.WarmStartCount}");
            await stdout.WriteLineAsync($"coldStart.count: {report.ColdStartCount}");

            if (report.WarmStartCount > 0)
            {
                await stdout.WriteLineAsync($"warmAcquire.minMs: {FormatLatency(report.WarmAcquireMinMs)}");
                await stdout.WriteLineAsync($"warmAcquire.medianMs: {FormatLatency(report.WarmAcquireMedianMs)}");
                await stdout.WriteLineAsync($"warmAcquire.p95Ms: {FormatLatency(report.WarmAcquireP95Ms)}");
                await stdout.WriteLineAsync($"warmAcquire.maxMs: {FormatLatency(report.WarmAcquireMaxMs)}");
            }

            if (report.ColdStartCount > 0)
            {
                await stdout.WriteLineAsync($"coldAcquire.minMs: {FormatLatency(report.ColdAcquireMinMs)}");
                await stdout.WriteLineAsync($"coldAcquire.medianMs: {FormatLatency(report.ColdAcquireMedianMs)}");
                await stdout.WriteLineAsync($"coldAcquire.p95Ms: {FormatLatency(report.ColdAcquireP95Ms)}");
                await stdout.WriteLineAsync($"coldAcquire.maxMs: {FormatLatency(report.ColdAcquireMaxMs)}");
            }

            await stdout.WriteLineAsync($"total.minMs: {FormatLatency(report.TotalLatencyMinMs)}");
            await stdout.WriteLineAsync($"total.medianMs: {FormatLatency(report.TotalLatencyMedianMs)}");
            await stdout.WriteLineAsync($"total.p95Ms: {FormatLatency(report.TotalLatencyP95Ms)}");
            await stdout.WriteLineAsync($"total.maxMs: {FormatLatency(report.TotalLatencyMaxMs)}");

            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ArgumentOutOfRangeException)
        {
            await stderr.WriteLineAsync($"Sandbox benchmark failed: {ex.Message}");
            return 1;
        }
    }

    private static bool TryParseArgs(string[] args, out SandboxBenchmarkCliRequest request, out string error)
    {
        request = default;
        error = string.Empty;

        int? iterations = null;
        var warmupIterations = 1_000;
        var poolSize = 4;
        var executorType = "process-v1";
        var containerImage = "aos-sandbox-worker:local";

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
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
                case "--pool-size" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out poolSize))
                    {
                        error = "The --pool-size value must be an integer.";
                        return false;
                    }

                    break;
                case "--executor" when i + 1 < args.Length:
                    executorType = args[++i];
                    break;
                case "--container-image" when i + 1 < args.Length:
                    containerImage = args[++i];
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

        if (executorType is not ("process-v1" or "container-v1"))
        {
            error = "The --executor value must be process-v1 or container-v1.";
            return false;
        }

        request = new SandboxBenchmarkCliRequest(
            iterations.Value,
            warmupIterations,
            poolSize,
            executorType,
            containerImage);
        return true;
    }

    private static string FormatLatency(double latencyMs) =>
        latencyMs.ToString("0.0000", CultureInfo.InvariantCulture);

    private static string GetUsageText() => """
        Usage: aos-replay benchmark-sandbox --iterations <count> [--warmup <count>] [--pool-size <count>] [--executor process-v1|container-v1] [--container-image <name>]

        Defaults: --warmup 1000 --pool-size 4 --executor process-v1 --container-image aos-sandbox-worker:local
        Exit codes: 0=reported, 1=benchmark failure, 2=usage/input failure
        """;

    private readonly record struct SandboxBenchmarkCliRequest(
        int Iterations,
        int WarmupIterations,
        int PoolSize,
        string ExecutorType,
        string ContainerImage
    );
}
