using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

// September's bounded demonstrator. New domains/profiles require explicit operator-owned configuration.
public static class RepositoryDiagnosticsDefaults
{
    public const string Revision = "repository-diagnostics-fixture-v1";
    public static readonly DateTimeOffset CreatedAt = new(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);
    public static DiagnosticsRecoverySettings Recovery(int retries = 1) =>
        new(retries, ["pricing"], new SortedDictionary<string, string>(StringComparer.Ordinal) { ["pricing.broad"] = "pricing.focused" });

    public static AllowedActionCatalog Catalog() => new(new[]
    {
        Action("repo.search", [("query", true), ("glob", false), ("limit", false)]),
        Action("repo.read", [("path", true), ("start", false), ("end", false)]),
        Action("repo.test", [("profileId", true)])
    });

    public static IReadOnlyList<PlannerPlaybook> Playbooks() =>
    [
        new("0.1", "pricing-diagnosis", "1", "repository.diagnosis", "Diagnose the focused pricing failure", ["pricing"],
        [
            Step("repo.search", ("query", "PriceCalculator")),
            Step("repo.read", ("path", "Pricing/PriceCalculator.cs")),
            Step("repo.read", ("path", "Pricing/PricingTests.cs")),
            Step("repo.test", ("profileId", "{{profileId}}"))
        ]),
        new("0.1", "configuration-trace", "1", "repository.diagnosis", "Trace environment configuration precedence", ["configuration"],
        [
            Step("repo.search", ("query", "SandboxPool")),
            Step("repo.read", ("path", "appsettings.json")),
            Step("repo.read", ("path", "appsettings.Development.json")),
            Step("repo.read", ("path", "Configuration/Program.cs"))
        ]),
        new("0.1", "path-probe", "1", "repository.diagnosis", "Read an explicitly requested relative path", ["path"],
            [Step("repo.read", ("path", "{{path}}"))]),
        new("0.1", "forbidden-probe", "1", "repository.diagnosis", "Untrusted forbidden-action test candidate", ["forbidden"],
            [new("repo.write", new Dictionary<string, string> { ["path"] = "appsettings.json", ["content"] = "unsafe" }),
             new("shell.exec", new Dictionary<string, string> { ["command"] = "touch unsafe" })])
    ];

    public static IReadOnlyList<DiagnosticPlaybookPolicy> Policies() =>
    [
        new("pricing-diagnosis", "1", "ApplyDiscount returns the discount amount (20) instead of the remaining price (80); its multiplication omits the complement of the discount.",
        [new("read", "Pricing/PriceCalculator.cs", "=> price * discount"),
         new("read", "Pricing/PricingTests.cs", "expected = 80m"),
         new("test", null, "FAIL PricingTests.ApplyDiscount_ReturnsRemainingPrice: expected 80, actual 20")]),
        new("configuration-trace", "1", "In Development, appsettings.Development.json overrides SandboxPool.PoolSize from 4 to 0 because CreateBuilder loads environment configuration after the base file.",
        [new("read", "appsettings.json", "\"PoolSize\": 4"),
         new("read", "appsettings.Development.json", "\"PoolSize\": 0"),
         new("read", "Configuration/Program.cs", "WebApplication.CreateBuilder")])
    ];

    public static IReadOnlyList<RepositoryTestProfile> Profiles(string hash, string workerAssembly) =>
    [new("pricing.focused", hash, Path.GetFullPath(workerAssembly), ["--repository-profile", "pricing.focused"], 2000),
     new("pricing.broad", hash, Path.GetFullPath(workerAssembly), ["--repository-profile", "pricing.broad"], 500)];

    public static IPlannerService Planner(AllowedActionCatalog catalog) => new DeterministicPlannerService(
        new DeterministicPlaybookRetriever(new InMemoryPlaybookStore(Playbooks())),
        new DeterministicPlaybookCandidateProvider(), new PlannerPlanValidator(catalog));

    private static AllowedActionDefinition Action(string id, (string Name, bool Required)[] args) =>
        new(id, new ToolRef(id, "1.0"),
        [new("snapshotId", true), new("snapshotHash", true), .. args.Select(a => new AllowedActionArgumentDefinition(a.Name, a.Required))]);
    private static PlannerPlaybookStep Step(string action, params (string Key, string Value)[] args)
    {
        var templates = new SortedDictionary<string, string>(StringComparer.Ordinal)
            { ["snapshotId"] = "{{snapshotId}}", ["snapshotHash"] = "{{snapshotHash}}" };
        foreach (var (key, value) in args) templates.Add(key, value);
        return new(action, templates);
    }
}
