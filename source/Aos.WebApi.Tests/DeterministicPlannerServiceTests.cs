using Aos.WebApi.Models;
using Aos.WebApi.Services;
using Xunit;

namespace Aos.WebApi.Tests;

public sealed class DeterministicPlannerServiceTests
{
    private static readonly AllowedActionDefinition EchoAction = new(
        "echo.message",
        new ToolRef("echo", "1.0"),
        [
            new AllowedActionArgumentDefinition("message", true),
            new AllowedActionArgumentDefinition("note", false)
        ]);

    [Fact]
    public void Plan_SelectsHighestScoreAndMaterializesDeterministicValidatedPlan()
    {
        var service = CreateService(
            new DeterministicPlaybookCandidateProvider(),
            CreatePlaybook("lower", ["audit"], [("echo.message", "message", "literal")]),
            CreatePlaybook("higher", ["audit", "replay"], [("echo.message", "message", "{{message}}")]
        ));

        var result = service.Plan(new PlannerTaskRequest(
            "task-1",
            "workflow.plan",
            [" REPLAY ", "audit", "audit"],
            new Dictionary<string, string> { ["message"] = "hello" }));

        Assert.Equal("validated", result.Status);
        Assert.Equal("higher", result.SelectedPlaybook!.Playbook.PlaybookId);
        Assert.Equal(["audit", "replay"], result.Task.Terms);
        Assert.Equal("task-1:plan:1", result.Plan!.PlanId);
        Assert.Equal("hello", result.Plan.Steps.Single().Arguments["message"]);
        Assert.True(result.Validation.IsValid);
    }

    [Fact]
    public void Plan_WhenTemplateBindingIsMissing_RejectsBeforeValidationOrExecution()
    {
        var service = CreateService(
            new DeterministicPlaybookCandidateProvider(),
            CreatePlaybook("missing", ["echo"], [("echo.message", "message", "{{message}}")]
        ));

        var result = service.Plan(new PlannerTaskRequest(
            "task-1", "workflow.plan", ["echo"], new Dictionary<string, string>()));

        Assert.Equal("rejected", result.Status);
        Assert.Equal("planner.template.argument_missing", result.ErrorCode);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void Plan_WhenNoPlaybookOverlaps_ReturnsStableNotFoundFailure()
    {
        var service = CreateService(
            new DeterministicPlaybookCandidateProvider(),
            CreatePlaybook("echo", ["echo"], [("echo.message", "message", "hello")]
        ));

        var result = service.Plan(new PlannerTaskRequest(
            "task-1", "workflow.plan", ["unrelated"], new Dictionary<string, string>()));

        Assert.Equal("rejected", result.Status);
        Assert.Equal("planner.playbook.not_found", result.ErrorCode);
    }

    [Fact]
    public void Plan_WithMalformedLocalModelOutput_RejectsUntrustedCandidate()
    {
        var service = CreateService(
            new LocalModelPlanCandidateProvider(new FixedLocalModelClient("not-json")),
            CreatePlaybook("echo", ["echo"], [("echo.message", "message", "hello")]
        ));

        var result = service.Plan(new PlannerTaskRequest(
            "task-1", "workflow.plan", ["echo"], new Dictionary<string, string>()));

        Assert.Equal("rejected", result.Status);
        Assert.Equal("planner.candidate.invalid_json", result.ErrorCode);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void Plan_WithParseableButDisallowedLocalModelAction_FailsClosed()
    {
        var json = """
            {"schemaVersion":"0.1","planId":"model-plan","taskClass":"workflow.plan","steps":[{"sequence":1,"stepId":"step-1","actionId":"admin.delete","arguments":{}}]}
            """;
        var service = CreateService(
            new LocalModelPlanCandidateProvider(new FixedLocalModelClient(json)),
            CreatePlaybook("echo", ["echo"], [("echo.message", "message", "hello")]
        ));

        var result = service.Plan(new PlannerTaskRequest(
            "task-1", "workflow.plan", ["echo"], new Dictionary<string, string>()));

        Assert.Equal("rejected", result.Status);
        Assert.Equal("planner.plan.invalid", result.ErrorCode);
        Assert.Contains(result.Validation.Errors, error => error.Code == "plan.step.action.not_allowed");
    }

    [Fact]
    public void Plan_WithLocalModelTaskClassMismatch_FailsClosed()
    {
        var json = """
            {"schemaVersion":"0.1","planId":"model-plan","taskClass":"workflow.other","steps":[{"sequence":1,"stepId":"step-1","actionId":"echo.message","arguments":{"message":"hello"}}]}
            """;
        var service = CreateService(
            new LocalModelPlanCandidateProvider(new FixedLocalModelClient(json)),
            CreatePlaybook("echo", ["echo"], [("echo.message", "message", "hello")]
        ));

        var result = service.Plan(new PlannerTaskRequest(
            "task-1", "workflow.plan", ["echo"], new Dictionary<string, string>()));

        Assert.Equal("rejected", result.Status);
        Assert.Contains(result.Validation.Errors, error => error.Code == "plan.task_class.mismatch");
    }

    private static DeterministicPlannerService CreateService(
        IPlannerCandidateProvider provider,
        params PlannerPlaybook[] playbooks)
    {
        var catalog = new AllowedActionCatalog([EchoAction]);
        return new DeterministicPlannerService(
            new DeterministicPlaybookRetriever(new InMemoryPlaybookStore(playbooks)),
            provider,
            new PlannerPlanValidator(catalog));
    }

    private static PlannerPlaybook CreatePlaybook(
        string id,
        IReadOnlyList<string> terms,
        params (string Action, string Argument, string Template)[] steps) =>
        new(
            PlannerSchemaVersions.CurrentPlaybookVersion,
            id,
            "1",
            "workflow.plan",
            $"Playbook {id}",
            terms,
            steps.Select(step => new PlannerPlaybookStep(
                step.Action,
                new Dictionary<string, string> { [step.Argument] = step.Template })).ToArray());

    private sealed class FixedLocalModelClient : ILocalModelPlannerClient
    {
        private readonly string _output;
        public FixedLocalModelClient(string output) => _output = output;
        public string GeneratePlanJson(PlannerTaskRequest task, PlannerPlaybook playbook) => _output;
    }
}
