namespace Aos.WebApi.Options;

public sealed class SandboxPoolOptions
{
    public const string SectionName = "SandboxPool";

    public int PoolSize { get; set; } = 4;
    public string ExecutorType { get; set; } = "container-v1";
    public string ContainerImage { get; set; } = "aos-sandbox-worker:local";
    public int MemoryLimitMb { get; set; } = 192;
    public decimal CpuLimit { get; set; } = 0.5m;
    public int PidsLimit { get; set; } = 32;
    public int TmpfsSizeMb { get; set; } = 16;
}
