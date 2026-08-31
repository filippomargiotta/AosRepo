using System.Text.Json;
using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public interface ILocalModelPlannerClient
{
    string GeneratePlanJson(PlannerTaskRequest task, PlannerPlaybook playbook);
}

public sealed class LocalModelPlanCandidateProvider : IPlannerCandidateProvider
{
    public const string SourceId = "local-model-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ILocalModelPlannerClient _client;

    public LocalModelPlanCandidateProvider(ILocalModelPlannerClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public PlannerCandidateResult CreateCandidate(PlannerTaskRequest task, PlaybookMatch selectedPlaybook)
    {
        try
        {
            var json = _client.GeneratePlanJson(task, selectedPlaybook.Playbook);
            var plan = JsonSerializer.Deserialize<PlannerPlan>(json, JsonOptions);
            return plan is null
                ? Invalid("Local-model output deserialized to null.")
                : new PlannerCandidateResult(SourceId, plan, null, null);
        }
        catch (JsonException ex)
        {
            return Invalid($"Local-model output is not valid planner JSON: {ex.Message}");
        }
    }

    private static PlannerCandidateResult Invalid(string error) =>
        new(SourceId, null, "planner.candidate.invalid_json", error);
}
