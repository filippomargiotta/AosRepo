using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Aos.WebApi.Services;

namespace Aos.WebApi;

public sealed class DiagnosticsRuntime : IDisposable
{
    private readonly PreWarmedSandboxPool _pool;
    public RepositorySnapshot Snapshot { get; }
    public DiagnosticsWorkflowService Workflow { get; }

    public DiagnosticsRuntime(string snapshotRoot, SandboxPoolOptions configured, int maxRetries,
        ICapabilityTokenIssuer issuer, ICapabilityTokenValidator validator, ISeedProvider seeds, ITimeSource clock,
        IEventLogIntegrityChain chain, IManifestSigner signer)
    {
        if (configured.ExecutorType != "container-v1")
            throw new InvalidOperationException("Repository diagnostics API requires the hardened container backend.");
        Snapshot = RepositoryDiagnosticsDefaults.ImportFixture(snapshotRoot);
        var options = new SandboxPoolOptions
        {
            PoolSize = configured.PoolSize, ExecutorType = configured.ExecutorType, ContainerImage = configured.ContainerImage,
            MemoryLimitMb = configured.MemoryLimitMb, CpuLimit = configured.CpuLimit, PidsLimit = configured.PidsLimit,
            TmpfsSizeMb = configured.TmpfsSizeMb, RepositorySnapshotDirectory = Path.GetFullPath(snapshotRoot)
        };
        _pool = new PreWarmedSandboxPool(options.PoolSize, SandboxSlotFactory.Create(options));
        var tools = new CapabilityEnforcingToolExecutor(validator,
            new PooledSandboxToolExecutor(_pool, Microsoft.Extensions.Options.Options.Create(options)));
        var catalog = RepositoryDiagnosticsDefaults.Catalog();
        Workflow = new DiagnosticsWorkflowService(RepositoryDiagnosticsDefaults.Planner(catalog), RepositoryDiagnosticsDefaults.Policies(),
            catalog, new PlannerStepExecutor(catalog, issuer, tools, clock), new RuleBasedDiagnosticsDecisions(),
            RepositoryDiagnosticsDefaults.Recovery(maxRetries), seeds, clock, new DiagnosticsRuleRouter(), chain, signer);
    }

    public void Dispose() => _pool.Dispose();
}
