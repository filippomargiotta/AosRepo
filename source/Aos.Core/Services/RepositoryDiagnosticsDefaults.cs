using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

// September's bounded demonstrator. New domains/profiles require explicit operator-owned configuration.
public static class RepositoryDiagnosticsDefaults
{
    public const string Revision = "repository-diagnostics-fixture-v1";
    public static readonly DateTimeOffset CreatedAt = new(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);
    public const string RevisionV2 = "repository-diagnostics-fixture-v2";
    public const string FixtureV2ContentHash = "ba6c6a7d43a9c3d029739228d7ff55cc78d1367e6e49f3cbca46f54dfd697eee";
    public static readonly DateTimeOffset CreatedAtV2 = new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);

    // Fixture v1 stays the default; the operator-pinned v2 content hash selects the v2 revision metadata.
    public static RepositorySnapshot ImportFixture(string root)
    {
        var snapshot = RepositorySnapshot.Import(root, Revision, CreatedAt);
        return snapshot.Reference.ContentHash == FixtureV2ContentHash
            ? RepositorySnapshot.Import(root, RevisionV2, CreatedAtV2) : snapshot;
    }
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
        new("0.1", "endpoint-trace", "1", "repository.diagnosis", "Trace an API endpoint to its service registration and implementation", ["endpoint"],
        [
            Step("repo.search", ("query", "api/pricing/quote")),
            Step("repo.read", ("path", "Api/PricingController.cs")),
            Step("repo.read", ("path", "Api/ServiceRegistration.cs")),
            Step("repo.read", ("path", "Pricing/DiscountQuoteService.cs"))
        ]),
        new("0.1", "dependency-chain", "1", "repository.diagnosis", "Explain which direct package introduces a flagged transitive package", ["dependency"],
        [
            Step("repo.search", ("query", "Newtonsoft.Json")),
            Step("repo.read", ("path", "Dependencies/RepositoryFixture.csproj.txt")),
            Step("repo.read", ("path", "Dependencies/project.assets.json")),
            Step("repo.read", ("path", "Dependencies/advisory-notes.txt"))
        ]),
        new("0.1", "replay-mismatch", "1", "repository.diagnosis", "Diagnose a recorded replay field mismatch", ["replay"],
        [
            Step("repo.search", ("query", "FirstMismatch")),
            Step("repo.read", ("path", "Replay/ReplayComparer.cs")),
            Step("repo.read", ("path", "Replay/last-replay-report.txt"))
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
         new("read", "Configuration/Program.cs", "WebApplication.CreateBuilder")]),
        new("endpoint-trace", "1", "GET api/pricing/quote is handled by PricingController.Quote, which depends on IQuoteService; ServiceRegistration.AddPricing registers DiscountQuoteService for it, and that service delegates to PriceCalculator.ApplyDiscount.",
        [new("read", "Api/PricingController.cs", "[HttpGet(\"quote\")]"),
         new("read", "Api/ServiceRegistration.cs", "AddScoped<IQuoteService, DiscountQuoteService>"),
         new("read", "Pricing/DiscountQuoteService.cs", "PriceCalculator.ApplyDiscount")]),
        new("dependency-chain", "1", "Newtonsoft.Json 9.0.1, which the recorded advisory flags, enters the project transitively through the direct package Contoso.Reports 2.1.0; the other direct package, Contoso.Telemetry, does not reference it.",
        [new("read", "Dependencies/RepositoryFixture.csproj.txt", "Include=\"Contoso.Reports\""),
         new("read", "Dependencies/project.assets.json", "\"Newtonsoft.Json\": \"9.0.1\""),
         new("read", "Dependencies/advisory-notes.txt", "Newtonsoft.Json below 13.0.1")]),
        new("replay-mismatch", "1", "Replay reports a mismatch in route.selectedModel: ReplayComparer compares the recorded selected model with the replayed one, and the report shows recorded local-small against replayed local-large because the replayed run selected its model from live router configuration instead of the recorded decision.",
        [new("read", "Replay/ReplayComparer.cs", "\"route.selectedModel\""),
         new("read", "Replay/last-replay-report.txt", "mismatch field=route.selectedModel recorded=local-small replayed=local-large")])
    ];

    public static IReadOnlyList<RepositoryTestProfile> Profiles(string hash, string workerAssembly) =>
    [new("pricing.focused", hash, Path.GetFullPath(workerAssembly), ["--repository-profile", "pricing.focused"], 2000),
     new("pricing.broad", hash, Path.GetFullPath(workerAssembly), ["--repository-profile", "pricing.broad"], 500),
     // Integration suite that fetches live exchange rates; catalogued so a request for it is denied, not unknown.
     new("pricing.live-rates", hash, Path.GetFullPath(workerAssembly), ["--repository-profile", "pricing.live-rates"], 2000, RequiresNetwork: true)];

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
