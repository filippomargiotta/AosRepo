using System.Text.Json;
using Aos.WebApi.Models;
using Aos.WebApi.Services;

if (args.Length == 2 && args[0] == "--repository-profile")
{
    if (args[1] == "pricing.broad") { await Task.Delay(TimeSpan.FromSeconds(30)); return 0; }
    if (args[1] == "pricing.focused") return RepositoryFixture.Pricing.PricingTests.RunFocused();
    return 2;
}

RepositoryToolExecutor? repositoryExecutor = null;
var snapshotRoot = Environment.GetEnvironmentVariable("AOS_REPOSITORY_SNAPSHOT_ROOT");
if (snapshotRoot is not null)
{
    var snapshot = RepositorySnapshot.Import(snapshotRoot, RepositoryDiagnosticsDefaults.Revision, RepositoryDiagnosticsDefaults.CreatedAt);
    var packaged = RepositorySnapshot.Import(Path.Combine(AppContext.BaseDirectory, "repository-fixture"),
        RepositoryDiagnosticsDefaults.Revision, RepositoryDiagnosticsDefaults.CreatedAt);
    repositoryExecutor = new RepositoryToolExecutor(snapshot,
        RepositoryDiagnosticsDefaults.Profiles(packaged.Reference.ContentHash, typeof(ProcessSandboxSlot).Assembly.Location.Replace("Aos.Core.dll", "Aos.SandboxWorker.dll")),
        new PackagedRepositoryTestRunner());
}

Console.Out.WriteLine(ProcessSandboxSlot.ReadyLine);
Console.Out.Flush();

var requestLine = await Console.In.ReadLineAsync();
if (string.IsNullOrWhiteSpace(requestLine))
{
    return 2;
}

ToolExecutionRequest? request;
try
{
    request = JsonSerializer.Deserialize<ToolExecutionRequest>(
        requestLine,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
catch (JsonException)
{
    return 3;
}

if (request is null)
{
    return 4;
}

if (TestCommandsAreEnabled())
{
    var command = TryReadSandboxTestCommand(request.InputJson);
    switch (command)
    {
        case "crash":
            return 101;
        case "timeout":
            await Task.Delay(TimeSpan.FromSeconds(30));
            return 102;
        case "stderr":
            for (var i = 0; i < 10_000; i++)
            {
                Console.Error.WriteLine("sandbox stderr drain test line");
            }

            break;
        case "filesystem-write":
            WriteResult(RunFilesystemWriteProbe(request));
            return 0;
        case "network":
            WriteResult(await RunNetworkProbeAsync(request));
            return 0;
        case "environment":
            WriteResult(RunEnvironmentProbe(request));
            return 0;
        case "state-write":
            WriteResult(RunStateWriteProbe(request));
            return 0;
        case "state-read":
            WriteResult(RunStateReadProbe(request));
            return 0;
        case "memory":
            WriteResult(RunMemoryProbe(request));
            return 0;
    }
}

var result = request.Tool.ToolId.StartsWith("repo.", StringComparison.Ordinal) || request.Action.StartsWith("repo.", StringComparison.Ordinal)
    ? repositoryExecutor?.Execute(request) ?? request.ToFailure("repo.snapshot.unavailable")
    : new DeterministicEchoToolExecutor().Execute(request);
WriteResult(result);
return 0;

static void WriteResult(ToolExecutionResult result)
{
    Console.Out.WriteLine(JsonSerializer.Serialize(
        result,
        new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    Console.Out.Flush();
}

static ToolExecutionResult RunFilesystemWriteProbe(ToolExecutionRequest request)
{
    var allowed = true;
    try
    {
        var root = Environment.GetEnvironmentVariable("AOS_REPOSITORY_SNAPSHOT_ROOT") ?? "/app";
        File.WriteAllText(Path.Combine(root, "sandbox-write-probe"), "denied");
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
    {
        allowed = false;
    }

    return ProbeResult(request, new { allowed });
}

static async Task<ToolExecutionResult> RunNetworkProbeAsync(ToolExecutionRequest request)
{
    var allowed = true;
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        using var response = await client.GetAsync("http://1.1.1.1");
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        allowed = false;
    }

    return ProbeResult(request, new { allowed });
}

static ToolExecutionResult RunEnvironmentProbe(ToolExecutionRequest request)
{
    var visible = Environment.GetEnvironmentVariable("AOS_HOST_SECRET_SHOULD_NOT_LEAK") is not null;
    return ProbeResult(request, new { visible });
}

static ToolExecutionResult RunStateWriteProbe(ToolExecutionRequest request)
{
    File.WriteAllText("/tmp/aos-tenant-state", request.RunId);
    return ProbeResult(request, new { written = true });
}

static ToolExecutionResult RunStateReadProbe(ToolExecutionRequest request)
{
    var visible = File.Exists("/tmp/aos-tenant-state");
    return ProbeResult(request, new { visible });
}

static ToolExecutionResult RunMemoryProbe(ToolExecutionRequest request)
{
    try
    {
        _ = GC.AllocateUninitializedArray<byte>(512 * 1024 * 1024);
        return ProbeResult(request, new { limited = false });
    }
    catch (OutOfMemoryException)
    {
        return request.ToFailure("sandbox_resource_limit");
    }
}

static ToolExecutionResult ProbeResult(ToolExecutionRequest request, object output) =>
    new(
        InvocationId: request.InvocationId,
        Tool: request.Tool,
        Status: "succeeded",
        InputJson: request.InputJson,
        OutputJson: JsonSerializer.Serialize(output, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        Error: null);

static string? TryReadSandboxTestCommand(string inputJson)
{
    try
    {
        using var document = JsonDocument.Parse(inputJson);
        if (document.RootElement.TryGetProperty("sandboxTest", out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            return property.GetString();
        }
    }
    catch (JsonException)
    {
    }

    return null;
}

static bool TestCommandsAreEnabled() =>
    string.Equals(
        Environment.GetEnvironmentVariable(ProcessSandboxSlot.TestCommandsEnvironmentVariable),
        "1",
        StringComparison.Ordinal);

static class ToolExecutionRequestExtensions
{
    public static ToolExecutionResult ToFailure(this ToolExecutionRequest request, string error) =>
        new(
            InvocationId: request.InvocationId,
            Tool: request.Tool,
            Status: "failed",
            InputJson: request.InputJson,
            OutputJson: "{}",
            Error: error);
}
