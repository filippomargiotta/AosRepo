using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Aos.WebApi.Models;
using Aos.WebApi.Options;

namespace Aos.WebApi.Services;

public sealed class ContainerSandboxSlot : ISandboxSlot
{
    public const string TestCommandsEnvironmentVariable = "AOS_SANDBOX_WORKER_TEST_COMMANDS";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultExecutionTimeout = TimeSpan.FromSeconds(5);

    private readonly Process _dockerProcess;
    private readonly TimeSpan _executionTimeout;
    private bool _disposed;

    public ContainerSandboxSlot(
        SandboxPoolOptions options,
        bool enableTestCommands = false,
        TimeSpan? startupTimeout = null,
        TimeSpan? executionTimeout = null)
    {
        ValidateOptions(options);
        _executionTimeout = executionTimeout ?? DefaultExecutionTimeout;
        ContainerName = $"aos-sandbox-{Guid.NewGuid():N}";
        _dockerProcess = StartContainer(options, ContainerName, enableTestCommands);
        _dockerProcess.ErrorDataReceived += (_, _) => { };
        _dockerProcess.BeginErrorReadLine();
        WaitForReady(startupTimeout ?? DefaultStartupTimeout);
    }

    public string ContainerName { get; }

    public string SlotId => $"container:{ContainerName}";

    public ToolExecutionResult Execute(ToolExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_dockerProcess.HasExited)
        {
            return CreateExitedFailure(request);
        }

        try
        {
            _dockerProcess.StandardInput.WriteLine(JsonSerializer.Serialize(request, JsonOptions));
        }
        catch (IOException)
        {
            return CreateExitedFailure(request);
        }
        catch (InvalidOperationException)
        {
            return CreateExitedFailure(request);
        }

        var outputTask = _dockerProcess.StandardOutput.ReadLineAsync();
        if (!outputTask.Wait(_executionTimeout))
        {
            StopContainer();
            return CreateFailure(request, "sandbox_timeout");
        }

        var responseLine = outputTask.GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(responseLine))
        {
            return CreateExitedFailure(request);
        }

        try
        {
            var result = JsonSerializer.Deserialize<ToolExecutionResult>(responseLine, JsonOptions);
            return IsValidResponse(request, result)
                ? result!
                : CreateFailure(request, "sandbox_protocol_error");
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
        StopContainer();
        _dockerProcess.Dispose();
    }

    private static Process StartContainer(
        SandboxPoolOptions options,
        string containerName,
        bool enableTestCommands)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        AddArguments(startInfo,
            "run", "--rm", "--interactive",
            "--name", containerName,
            "--network", "none",
            "--read-only",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges:true",
            "--pids-limit", options.PidsLimit.ToString(CultureInfo.InvariantCulture),
            "--memory", $"{options.MemoryLimitMb}m",
            "--memory-swap", $"{options.MemoryLimitMb}m",
            "--cpus", options.CpuLimit.ToString(CultureInfo.InvariantCulture),
            "--tmpfs", $"/tmp:rw,noexec,nosuid,nodev,size={options.TmpfsSizeMb}m",
            "--env", "HOME=/tmp",
            "--env", "DOTNET_EnableDiagnostics=0");

        if (enableTestCommands)
        {
            AddArguments(startInfo, "--env", $"{TestCommandsEnvironmentVariable}=1");
        }

        startInfo.ArgumentList.Add(options.ContainerImage);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Sandbox container process failed to start.");
    }

    private static void AddArguments(ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    private void WaitForReady(TimeSpan startupTimeout)
    {
        var readyTask = _dockerProcess.StandardOutput.ReadLineAsync();
        if (!readyTask.Wait(startupTimeout))
        {
            StopContainer();
            throw new InvalidOperationException("Sandbox container did not become ready before the startup timeout.");
        }

        var line = readyTask.GetAwaiter().GetResult();
        if (!string.Equals(line, ProcessSandboxSlot.ReadyLine, StringComparison.Ordinal))
        {
            StopContainer();
            throw new InvalidOperationException(
                "Sandbox container failed the startup protocol. Build the configured worker image first.");
        }
    }

    private ToolExecutionResult CreateExitedFailure(ToolExecutionRequest request)
    {
        if (_dockerProcess.HasExited && _dockerProcess.ExitCode == 137)
        {
            return CreateFailure(request, "sandbox_resource_limit");
        }

        return CreateFailure(request, "sandbox_process_exit");
    }

    private void StopContainer()
    {
        try
        {
            if (!_dockerProcess.HasExited)
            {
                using var removeProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = "docker",
                    ArgumentList = { "rm", "--force", ContainerName },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                removeProcess?.WaitForExit(milliseconds: 5_000);
            }
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            if (!_dockerProcess.HasExited)
            {
                _dockerProcess.Kill(entireProcessTree: true);
                _dockerProcess.WaitForExit(milliseconds: 1_000);
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

    private static bool IsValidResponse(ToolExecutionRequest request, ToolExecutionResult? result) =>
        result is not null
        && string.Equals(result.InvocationId, request.InvocationId, StringComparison.Ordinal)
        && string.Equals(result.Tool.ToolId, request.Tool.ToolId, StringComparison.Ordinal)
        && string.Equals(result.Tool.Version, request.Tool.Version, StringComparison.Ordinal)
        && string.Equals(result.InputJson, request.InputJson, StringComparison.Ordinal);

    private static void ValidateOptions(SandboxPoolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ContainerImage))
        {
            throw new ArgumentException("Sandbox container image is required.", nameof(options));
        }

        if (options.MemoryLimitMb <= 0 || options.CpuLimit <= 0 || options.PidsLimit <= 0 || options.TmpfsSizeMb <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Sandbox resource limits must be greater than zero.");
        }
    }
}
