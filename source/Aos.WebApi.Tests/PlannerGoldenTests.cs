using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Aos.WebApi.Services;
using Xunit;

namespace Aos.WebApi.Tests;

public sealed class PlannerGoldenTests
{
    private static readonly DateTimeOffset GoldenInstant =
        new(2026, 8, 31, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RecordPlannerWorkflows_MatchCheckedInGoldenArtifacts()
    {
        foreach (var scenario in CreateScenarios())
        {
            var artifacts = CreateArtifacts(scenario);
            UpdateGoldenWhenRequested(scenario.Name, artifacts);

            Assert.Equal(
                GoldenArtifactTestSupport.ReadGoldenManifestJson(scenario.Name),
                GoldenArtifactTestSupport.SerializeManifestRecord(artifacts.ManifestRecord));
            Assert.Equal(
                GoldenArtifactTestSupport.ReadGoldenEventLogJsonl(scenario.Name),
                GoldenArtifactTestSupport.SerializeEventLogLines(artifacts.EventLogRecords));
            Assert.Equal("succeeded", artifacts.Execution.Status);
        }
    }

    [Fact]
    public void PlannerArtifacts_AreSignedAndContainValidatedPlanBeforeToolEvidence()
    {
        var artifacts = CreateArtifacts(CreateScenarios()[1]);
        var signer = new HmacManifestSigner(
            GoldenArtifactTestSupport.GoldenHmacKey,
            GoldenArtifactTestSupport.GoldenHmacKeyId);
        var chain = new HmacEventLogIntegrityChain(
            GoldenArtifactTestSupport.GoldenHmacKey,
            GoldenArtifactTestSupport.GoldenHmacKeyId);

        Assert.True(signer.TryValidateRecord(artifacts.ManifestRecord, out var manifestError));
        Assert.Null(manifestError);
        Assert.True(chain.TryValidateRecords(artifacts.EventLogRecords, out var logError));
        Assert.Null(logError);
        Assert.Equal(
            ["planner.plan", "tool.execution", "tool.execution", "workflow.planner"],
            artifacts.EventLogRecords.Select(record => record.Entry.EventType));
        var planEvent = Assert.IsType<PlannerPlanEvent>(artifacts.EventLogRecords[0].Entry.Data);
        Assert.True(planEvent.Validation.IsValid);
        Assert.Equal(2, planEvent.Plan.Steps.Count);
        Assert.Equal([1, 2], artifacts.Execution.Steps.Select(step => step.Sequence));
    }

    private static PlannerWorkflowArtifacts CreateArtifacts(PlannerGoldenScenario scenario)
    {
        var actionCatalog = new AllowedActionCatalog([CreateAction()]);
        var store = new InMemoryPlaybookStore(scenario.Playbooks);
        var planner = new DeterministicPlannerService(
            new DeterministicPlaybookRetriever(store),
            new DeterministicPlaybookCandidateProvider(),
            new PlannerPlanValidator(actionCatalog));
        var capabilityTokenService = CapabilityTestData.CreateTokenService();
        var callCount = scenario.ExpectedStepCount + 3;
        var timeSource = new RecordingTimeSource(new FixedSequenceTimeSource(
            Enumerable.Repeat(GoldenInstant, callCount),
            new TimeSourceInfo(
                "record",
                "golden-fixed",
                $"clock-{scenario.Name}",
                "utc-millis",
                "planner golden fixture")));
        var stepExecutor = new PlannerStepExecutor(
            actionCatalog,
            capabilityTokenService,
            CapabilityTestData.CreateEnforcingExecutor(capabilityTokenService),
            timeSource);
        var service = new PlannerWorkflowService(
            new FixedSeedProvider(new SeedInfo(
                $"seed-{scenario.RunId}",
                "test-sequence",
                scenario.Seed,
                "golden-fixed"), preserveSeedId: true),
            timeSource,
            Microsoft.Extensions.Options.Options.Create(CreateOptions()),
            new FixedRouterService(CreateRoutingDecision(), "workflow.plan"),
            planner,
            stepExecutor,
            actionCatalog,
            new HmacEventLogIntegrityChain(
                GoldenArtifactTestSupport.GoldenHmacKey,
                GoldenArtifactTestSupport.GoldenHmacKeyId),
            new HmacManifestSigner(
                GoldenArtifactTestSupport.GoldenHmacKey,
                GoldenArtifactTestSupport.GoldenHmacKeyId));

        var artifacts = service.CreateArtifacts(scenario.RunId, scenario.Task);
        Assert.Equal(callCount, timeSource.GetRecordedInstants().Count);
        return artifacts;
    }

    private static AllowedActionDefinition CreateAction() =>
        new(
            "echo.message",
            new ToolRef("planner-echo", "1.0"),
            [
                new AllowedActionArgumentDefinition("message", true),
                new AllowedActionArgumentDefinition("note", false)
            ]);

    private static PlannerWorkflowOptions CreateOptions() => new()
    {
        Routing = new PlannerRoutingOptions
        {
            MaxLatencyMs = 100,
            MaxCostPer1KTokens = 0.1m,
            MinQualityScore = 50,
            RequiredComplianceTags = ["standard"]
        },
        PolicyDecisions =
        [
            new HelloWorkflowPolicyOptions
            {
                PolicyId = "planner-validated-actions-only",
                Decision = "allow",
                Reason = "plan passed the allowed-action validator"
            }
        ]
    };

    private static RouterSelectionResult CreateRoutingDecision()
    {
        var candidate = RouterTestData.CreateCandidate(
            modelId: "local-planner",
            provider: "local",
            version: "1.0",
            complianceTags: ["standard"]);
        return RouterTestData.CreateRoutingDecision(
            taskClass: "workflow.plan",
            policyId: "golden-planner-policy",
            candidate: candidate,
            maxLatencyMs: 100,
            maxCostPer1KTokens: 0.1m,
            minQualityScore: 50,
            requiredComplianceTags: ["standard"],
            effectiveWeights: new RouterSelectionWeights(0.25m, 0.25m, 0.25m, 0.25m),
            score: 0.9m);
    }

    private static IReadOnlyList<PlannerGoldenScenario> CreateScenarios() =>
    [
        new(
            "planner-exact-one-step-v1",
            "run-planner-exact-1",
            5101,
            new PlannerTaskRequest(
                "task-exact",
                "workflow.plan",
                ["exact", "echo"],
                new Dictionary<string, string> { ["message"] = "hello exact" }),
            [CreatePlaybook("exact-one", ["exact", "echo"], Step("{{message}}"))],
            1),
        new(
            "planner-two-step-v1",
            "run-planner-two-step-1",
            5102,
            new PlannerTaskRequest(
                "task-two-step",
                "workflow.plan",
                ["sequence", "two-step"],
                new Dictionary<string, string> { ["message"] = "first" }),
            [CreatePlaybook("two-step", ["sequence", "two-step"], Step("{{message}}"), Step("complete"))],
            2),
        new(
            "planner-highest-score-v1",
            "run-planner-highest-1",
            5103,
            new PlannerTaskRequest(
                "task-highest",
                "workflow.plan",
                ["audit", "replay"],
                new Dictionary<string, string> { ["message"] = "highest score" }),
            [
                CreatePlaybook("lower-score", ["audit"], Step("lower")),
                CreatePlaybook("higher-score", ["audit", "replay"], Step("{{message}}"))
            ],
            1),
        new(
            "planner-lexical-tie-v1",
            "run-planner-tie-1",
            5104,
            new PlannerTaskRequest(
                "task-tie",
                "workflow.plan",
                ["tie", "echo"],
                new Dictionary<string, string>()),
            [
                CreatePlaybook("zeta", ["tie", "echo"], Step("zeta")),
                CreatePlaybook("alpha", ["echo", "tie"], Step("alpha"))
            ],
            1),
        new(
            "planner-optional-arguments-v1",
            "run-planner-optional-1",
            5105,
            new PlannerTaskRequest(
                "task-optional",
                "workflow.plan",
                ["optional", "note"],
                new Dictionary<string, string>
                {
                    ["message"] = "required value",
                    ["note"] = "optional value"
                }),
            [CreatePlaybook(
                "optional-arguments",
                ["optional", "note"],
                new PlannerPlaybookStep(
                    "echo.message",
                    new Dictionary<string, string>
                    {
                        ["message"] = "{{message}}",
                        ["note"] = "{{note}}"
                    }))],
            1)
    ];

    private static PlannerPlaybookStep Step(string message) =>
        new("echo.message", new Dictionary<string, string> { ["message"] = message });

    private static PlannerPlaybook CreatePlaybook(
        string id,
        IReadOnlyList<string> terms,
        params PlannerPlaybookStep[] steps) =>
        new(
            PlannerSchemaVersions.CurrentPlaybookVersion,
            id,
            "1",
            "workflow.plan",
            $"Planner golden playbook {id}",
            terms,
            steps);

    private static void UpdateGoldenWhenRequested(string scenario, PlannerWorkflowArtifacts artifacts)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("AOS_UPDATE_GOLDEN"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var directory = GoldenArtifactTestSupport.GetGoldenDir(scenario);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "scenario.json"),
            $"{{\"name\":\"{scenario}\",\"workflow\":\"planner\",\"manifestPath\":\"manifest.json\",\"eventLogPath\":\"eventlog.jsonl\"}}\n");
        File.WriteAllText(
            Path.Combine(directory, "manifest.json"),
            GoldenArtifactTestSupport.SerializeManifestRecord(artifacts.ManifestRecord));
        File.WriteAllText(
            Path.Combine(directory, "eventlog.jsonl"),
            GoldenArtifactTestSupport.SerializeEventLogLines(artifacts.EventLogRecords));
    }

    private sealed record PlannerGoldenScenario(
        string Name,
        string RunId,
        long Seed,
        PlannerTaskRequest Task,
        IReadOnlyList<PlannerPlaybook> Playbooks,
        int ExpectedStepCount);
}
