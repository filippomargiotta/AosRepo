using Aos.ReplayCli;

if (args.Length > 0 && string.Equals(args[0], "evaluate", StringComparison.OrdinalIgnoreCase))
{
    return await GoldenEvaluationRunner.RunAsync(args[1..], Console.Out, Console.Error, CancellationToken.None);
}

if (args.Length > 0 && string.Equals(args[0], "benchmark-router", StringComparison.OrdinalIgnoreCase))
{
    return await RouterBenchmarkCliRunner.RunAsync(args[1..], Console.Out, Console.Error, CancellationToken.None);
}

if (args.Length > 0 && string.Equals(args[0], "benchmark-sandbox", StringComparison.OrdinalIgnoreCase))
{
    return await SandboxBenchmarkCliRunner.RunAsync(args[1..], Console.Out, Console.Error, CancellationToken.None);
}

return await ReplayCliRunner.RunAsync(args, Console.Out, Console.Error, CancellationToken.None);
