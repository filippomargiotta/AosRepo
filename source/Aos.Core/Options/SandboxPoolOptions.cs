namespace Aos.WebApi.Options;

public sealed class SandboxPoolOptions
{
    public const string SectionName = "SandboxPool";

    public int PoolSize { get; set; } = 4;
    public string ExecutorType { get; set; } = "process-v1";
}
