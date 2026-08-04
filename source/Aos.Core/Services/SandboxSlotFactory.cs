using Aos.WebApi.Options;

namespace Aos.WebApi.Services;

public static class SandboxSlotFactory
{
    public static Func<ISandboxSlot> Create(SandboxPoolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.ExecutorType switch
        {
            "process-v1" => () => new ProcessSandboxSlot(),
            "container-v1" => () => new ContainerSandboxSlot(options),
            _ => throw new InvalidOperationException(
                $"Unsupported sandbox executor type '{options.ExecutorType}'.")
        };
    }
}
