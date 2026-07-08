using System.Diagnostics;
using System.Text.Json;
using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public sealed class ProcessSandboxSlot : IDisposable
{
    public const string ReadyLine = "__aos_sandbox_worker_ready_v1";
    public const string TestCommandsEnvironmentVariable = "AOS_SANDBOX_WORKER_TEST_COMMANDS";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultExecutionTimeout = TimeSpan.FromSeconds(5);

    private readonly Process _process;
    private readonly TimeSpan _executionTimeout;
    private bool _disposed;

    public ProcessSandboxSlot()
        : this(workerAssemblyPath: null, DefaultStartupTimeout, DefaultExecutionTimeout)
    {
    }

    public ProcessSandboxSlot(
        string? workerAssemblyPath = null,
        TimeSpan? startupTimeout = null,
        TimeSpan? executionTimeout = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        _executionTimeout = executionTimeout ?? DefaultExecutionTimeout;
        _process = StartWorker(workerAssemblyPath ?? ResolveDefaultWorkerAssemblyPath(), environmentVariables);
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginErrorReadLine();
        WaitForReady(startupTimeout ?? DefaultStartupTimeout);
    }

    public int ProcessId => _process.Id;

    public bool HasExited => _process.HasExited;

    public ToolExecutionResult Execute(ToolExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_process.HasExited)
        {
            return CreateFailure(request, "sandbox_process_exit");
        }

        try
        {
            _process.StandardInput.WriteLine(JsonSerializer.Serialize(request, JsonOptions));
        }
        catch (IOException)
        {
            return CreateFailure(request, "sandbox_process_exit");
        }
        catch (InvalidOperationException)
        {
            return CreateFailure(request, "sandbox_process_exit");
        }

        var outputTask = _process.StandardOutput.ReadLineAsync();
        if (!outputTask.Wait(_executionTimeout))
        {
            TryKillProcess();
            return CreateFailure(request, "sandbox_timeout");
        }

        var responseLine = outputTask.GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(responseLine))
        {
            return CreateFailure(request, "sandbox_process_exit");
        }

        try
        {
            var result = JsonSerializer.Deserialize<ToolExecutionResult>(responseLine, JsonOptions);
            return result ?? CreateFailure(request, "sandbox_protocol_error");
        }
        catch (JsonException)
        {
            return CreateFailure(request, "sandbox_protocol_error");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TryKillProcess();
        _process.Dispose();
    }

    private static Process StartWorker(
        string workerAssemblyPath,
        IReadOnlyDictionary<string, string>? environmentVariables)
    {
        if (!File.Exists(workerAssemblyPath))
        {
            throw new InvalidOperationException(
                $"Sandbox worker assembly was not found at '{workerAssemblyPath}'.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(workerAssemblyPath);

        if (environmentVariables is not null)
        {
            foreach (var (key, value) in environmentVariables)
            {
                startInfo.Environment[key] = value;
            }
        }

        var process = Process.Start(startInfo);
        return process ?? throw new InvalidOperationException("Sandbox worker process failed to start.");
    }

    private static string ResolveDefaultWorkerAssemblyPath() =>
        Path.Combine(AppContext.BaseDirectory, "Aos.SandboxWorker.dll");

    private void WaitForReady(TimeSpan startupTimeout)
    {
        var readyTask = _process.StandardOutput.ReadLineAsync();
        if (!readyTask.Wait(startupTimeout))
        {
            TryKillProcess();
            throw new InvalidOperationException("Sandbox worker did not become ready before the startup timeout.");
        }

        var line = readyTask.GetAwaiter().GetResult();
        if (!string.Equals(line, ReadyLine, StringComparison.Ordinal))
        {
            TryKillProcess();
            throw new InvalidOperationException("Sandbox worker failed the startup protocol.");
        }
    }

    private void TryKillProcess()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(milliseconds: 1_000);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static ToolExecutionResult CreateFailure(ToolExecutionRequest request, string error) =>
        new(
            InvocationId: request.InvocationId,
            Tool: request.Tool,
            Status: "failed",
            InputJson: request.InputJson,
            OutputJson: "{}",
            Error: error);
}
