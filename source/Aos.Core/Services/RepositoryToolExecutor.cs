using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

// RequiresNetwork is an operator declaration; diagnostics never grants network, so such profiles are denied unexecuted.
public sealed record RepositoryTestProfile(
    string Id, string SnapshotHash, string AssemblyPath, IReadOnlyList<string> Arguments, int TimeoutMs, bool RequiresNetwork = false);
public sealed record RepositoryTestRun(int? ExitCode, string Stdout, string Stderr, string? Failure, double DurationMs);
public interface IRepositoryTestRunner
{
    RepositoryTestRun Run(RepositoryTestProfile profile);
}

// Profiles are operator-owned configuration. No command or executable comes from a request/snapshot.
public sealed class PackagedRepositoryTestRunner : IRepositoryTestRunner
{
    public RepositoryTestRun Run(RepositoryTestProfile profile)
    {
        var timer = Stopwatch.StartNew();
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(profile.AssemblyPath))!
        };
        start.Environment.Clear();
        start.Environment["DOTNET_EnableDiagnostics"] = "0";
        start.ArgumentList.Add(Path.GetFullPath(profile.AssemblyPath));
        foreach (var arg in profile.Arguments) start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Test runner did not start.");
            using var limit = new CancellationTokenSource(profile.TimeoutMs);
            var stdout = ReadBoundedAsync(process.StandardOutput, limit.Token);
            var stderr = ReadBoundedAsync(process.StandardError, limit.Token);
            string? failure = null;
            try { process.WaitForExitAsync(limit.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException)
            {
                failure = "repo.test.timeout";
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
            }
            var output = stdout.GetAwaiter().GetResult();
            var error = stderr.GetAwaiter().GetResult();
            if (output.Truncated || error.Truncated) failure ??= "repo.output.limit";
            return new RepositoryTestRun(failure is null ? process.ExitCode : null,
                output.Text, error.Text, failure, timer.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new RepositoryTestRun(null, "", "", "repo.test.runner_failed", timer.Elapsed.TotalMilliseconds);
        }
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[512];
        var result = new StringBuilder();
        var truncated = false;
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
            {
                var remaining = 2048 - result.Length;
                if (count > remaining) truncated = true;
                if (remaining > 0) result.Append(buffer, 0, Math.Min(count, remaining));
            }
        }
        catch (OperationCanceledException) { }
        return (result.ToString().Replace("\r\n", "\n"), truncated);
    }
}

public sealed class RepositoryToolExecutor : IToolExecutor
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RepositorySnapshot _snapshot;
    private readonly IReadOnlyDictionary<string, RepositoryTestProfile> _profiles;
    private readonly IRepositoryTestRunner _runner;

    public RepositoryToolExecutor(RepositorySnapshot snapshot, IEnumerable<RepositoryTestProfile> profiles, IRepositoryTestRunner runner)
    {
        _snapshot = snapshot;
        _profiles = profiles.ToDictionary(p => p.Id, StringComparer.Ordinal);
        if (_profiles.Values.Any(p => p.TimeoutMs is < 1 or > 2000 || !Path.IsPathRooted(p.AssemblyPath)))
            throw new ArgumentException("Profiles need an absolute packaged runner path and a 1–2000ms timeout.");
        _runner = runner;
    }

    public ToolExecutionResult Execute(ToolExecutionRequest request)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            if (request.Tool.ToolId != request.Action || request.Tool.Version != "1.0" ||
                request.Action is not ("repo.search" or "repo.read" or "repo.test"))
                throw new RepositoryPolicyException("repo.action.denied");
            if (request.InputJson.Length > 4096) throw new RepositoryPolicyException("repo.input.limit");
            var input = JsonSerializer.Deserialize<Dictionary<string, string>>(request.InputJson, JsonOptions)
                ?? throw new RepositoryPolicyException("repo.input.invalid");
            var permitted = request.Action switch
            {
                "repo.search" => new[] { "snapshotId", "snapshotHash", "query", "glob", "limit" },
                "repo.read" => ["snapshotId", "snapshotHash", "path", "start", "end"],
                _ => ["snapshotId", "snapshotHash", "profileId"]
            };
            if (input.Keys.Any(k => !permitted.Contains(k, StringComparer.Ordinal)))
                throw new RepositoryPolicyException("repo.argument.denied");
            if (Get(input, "snapshotId") != _snapshot.Reference.SnapshotId ||
                Get(input, "snapshotHash") != _snapshot.Reference.ContentHash)
                throw new RepositoryPolicyException("repo.snapshot.mismatch");
            RepositoryToolOutput output;
            string? error = null;
            if (request.Action == "repo.read")
                output = BoundLines("read", _snapshot.Read(Get(input, "path"), Number(input, "start", 1), Number(input, "end", 100)), 100);
            else if (request.Action == "repo.search")
            {
                var limit = Number(input, "limit", 20);
                if (limit is < 1 or > 100) throw new RepositoryPolicyException("repo.bounds.invalid");
                output = BoundLines("search", _snapshot.Search(Get(input, "query"), input.GetValueOrDefault("glob")), limit);
            }
            else
            {
                var id = Get(input, "profileId");
                if (!_profiles.TryGetValue(id, out var profile)) throw new RepositoryPolicyException("repo.profile.denied");
                if (profile.SnapshotHash != _snapshot.Reference.ContentHash) throw new RepositoryPolicyException("repo.profile.snapshot_mismatch");
                if (profile.RequiresNetwork) throw new RepositoryPolicyException("repo.network.denied");
                var run = _runner.Run(profile);
                error = run.Failure;
                output = new RepositoryToolOutput(_snapshot.Reference, "test", [], false, id,
                    run.ExitCode, run.Stdout, run.Stderr, run.Failure ?? (run.ExitCode == 0 ? "passed" : "test_failed"));
                timer.Stop();
                // Runtime measurements are outside signed payloads, as with existing sandbox timing.
                return Result(request, output, error) with
                { SandboxExecution = new SandboxExecutionInfo(false, 0, run.DurationMs, "repository-runner") };
            }
            return Result(request, output, error) with
            { SandboxExecution = new SandboxExecutionInfo(false, 0, timer.Elapsed.TotalMilliseconds, "repository-tools") };
        }
        catch (RepositoryPolicyException ex)
        {
            return new ToolExecutionResult(request.InvocationId, request.Tool, "denied", request.InputJson, "{}", ex.ReasonCode);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException or ArgumentException)
        {
            return new ToolExecutionResult(request.InvocationId, request.Tool, "denied", request.InputJson, "{}", "repo.input.invalid");
        }
    }

    private RepositoryToolOutput BoundLines(string kind, IEnumerable<RepositoryLine> candidates, int max)
    {
        var lines = new List<RepositoryLine>();
        var bytes = 0;
        var truncated = false;
        foreach (var line in candidates)
        {
            var size = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(line, JsonOptions));
            if (lines.Count >= max || bytes + size > 4096) { truncated = true; break; }
            bytes += size;
            lines.Add(line);
        }
        return new RepositoryToolOutput(_snapshot.Reference, kind, lines.AsReadOnly(), truncated);
    }

    private static ToolExecutionResult Result(ToolExecutionRequest request, RepositoryToolOutput output, string? error) =>
        new(request.InvocationId, request.Tool, error is null ? "succeeded" : "failed", request.InputJson,
            JsonSerializer.Serialize(output, JsonOptions), error);
    private static string Get(IReadOnlyDictionary<string, string> input, string key) =>
        input.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new RepositoryPolicyException("repo.input.invalid");
    private static int Number(IReadOnlyDictionary<string, string> input, string key, int fallback) =>
        input.TryGetValue(key, out var value) ? int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture) : fallback;
}
