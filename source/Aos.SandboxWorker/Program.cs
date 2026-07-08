using System.Text.Json;
using Aos.WebApi.Models;
using Aos.WebApi.Services;

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
    }
}

var result = new DeterministicEchoToolExecutor().Execute(request);
Console.Out.WriteLine(JsonSerializer.Serialize(
    result,
    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
Console.Out.Flush();
return 0;

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
