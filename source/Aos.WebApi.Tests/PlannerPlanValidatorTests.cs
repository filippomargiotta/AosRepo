using Aos.WebApi.Models;
using Aos.WebApi.Services;
using Xunit;

namespace Aos.WebApi.Tests;

public sealed class PlannerPlanValidatorTests
{
    [Fact]
    public void Validate_WithAllowedActionsAndArguments_ReturnsValid()
    {
        var validator = CreateValidator();
        var plan = new PlannerPlan(
            SchemaVersion: PlannerSchemaVersions.CurrentPlanVersion,
            PlanId: "plan-1",
            TaskClass: "workflow.hello",
            Steps:
            [
                new PlannerPlanStep(
                    Sequence: 1,
                    StepId: "step-1",
                    ActionId: "echo.execute",
                    Arguments: new Dictionary<string, string>
                    {
                        ["input"] = "hello",
                        ["format"] = "json"
                    })
            ]);

        var result = validator.Validate(plan);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_NullPlan_ReturnsStableRequiredError()
    {
        var result = CreateValidator().Validate(null);

        var error = Assert.Single(result.Errors);
        Assert.False(result.IsValid);
        Assert.Equal("plan.required", error.Code);
        Assert.Equal("$", error.Path);
    }

    [Fact]
    public void Validate_InvalidPlan_ReturnsErrorsInDeterministicOrder()
    {
        var validator = CreateValidator();
        var plan = new PlannerPlan(
            SchemaVersion: "9.9",
            PlanId: "plan-1",
            TaskClass: "workflow.hello",
            Steps:
            [
                new PlannerPlanStep(
                    Sequence: 2,
                    StepId: "duplicate",
                    ActionId: "echo.execute",
                    Arguments: new Dictionary<string, string>
                    {
                        ["zeta"] = "z",
                        ["alpha"] = "a"
                    }),
                new PlannerPlanStep(
                    Sequence: 2,
                    StepId: "duplicate",
                    ActionId: "echo.execute",
                    Arguments: new Dictionary<string, string>
                    {
                        ["input"] = "hello"
                    })
            ]);

        var result = validator.Validate(plan);

        Assert.False(result.IsValid);
        Assert.Equal(
        [
            "plan.schema_version.unsupported",
            "plan.step.sequence.invalid",
            "plan.step.argument.not_allowed",
            "plan.step.argument.not_allowed",
            "plan.step.argument.required",
            "plan.step.id.duplicate"
        ], result.Errors.Select(error => error.Code));
        Assert.Equal(
        [
            "steps[0].arguments.alpha",
            "steps[0].arguments.zeta"
        ], result.Errors
            .Where(error => error.Code == "plan.step.argument.not_allowed")
            .Select(error => error.Path));
    }

    [Fact]
    public void Validate_UnknownOrDifferentlyCasedAction_FailsClosed()
    {
        var plan = CreateValidPlan() with
        {
            Steps =
            [
                new PlannerPlanStep(
                    Sequence: 1,
                    StepId: "step-1",
                    ActionId: "Echo.Execute",
                    Arguments: new Dictionary<string, string> { ["input"] = "hello" })
            ]
        };

        var result = CreateValidator().Validate(plan);

        var error = Assert.Single(result.Errors);
        Assert.Equal("plan.step.action.not_allowed", error.Code);
        Assert.Equal("steps[0].actionId", error.Path);
    }

    [Fact]
    public void Constructor_DuplicateActionIds_ThrowsStableConfigurationError()
    {
        var action = CreateAllowedAction();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new PlannerPlanValidator([action, action]));

        Assert.Equal("Allowed actions contain duplicate action ids: echo.execute.", exception.Message);
    }

    [Fact]
    public void Constructor_SnapshotsAllowedActionArguments()
    {
        var mutableArguments = new List<AllowedActionArgumentDefinition>
        {
            new("input", Required: true)
        };
        var validator = new PlannerPlanValidator(
        [
            new AllowedActionDefinition(
                "echo.execute",
                new ToolRef("deterministic-echo", "1.0"),
                mutableArguments)
        ]);
        mutableArguments.Clear();

        var result = validator.Validate(CreateValidPlan() with
        {
            Steps =
            [
                new PlannerPlanStep(
                    Sequence: 1,
                    StepId: "step-1",
                    ActionId: "echo.execute",
                    Arguments: new Dictionary<string, string>())
            ]
        });

        var error = Assert.Single(result.Errors);
        Assert.Equal("plan.step.argument.required", error.Code);
    }

    private static PlannerPlanValidator CreateValidator() =>
        new([CreateAllowedAction()]);

    private static AllowedActionDefinition CreateAllowedAction() =>
        new(
            ActionId: "echo.execute",
            Tool: new ToolRef("deterministic-echo", "1.0"),
            Arguments:
            [
                new AllowedActionArgumentDefinition("input", Required: true),
                new AllowedActionArgumentDefinition("format", Required: false)
            ]);

    private static PlannerPlan CreateValidPlan() =>
        new(
            SchemaVersion: PlannerSchemaVersions.CurrentPlanVersion,
            PlanId: "plan-1",
            TaskClass: "workflow.hello",
            Steps:
            [
                new PlannerPlanStep(
                    Sequence: 1,
                    StepId: "step-1",
                    ActionId: "echo.execute",
                    Arguments: new Dictionary<string, string> { ["input"] = "hello" })
            ]);
}
