using System.Text.Json;
using Aos.WebApi.Models;

namespace Aos.WebApi.Services;

public interface IDiagnosticsDecisions
{
    DiagnosticsCriticDecision Assess(DiagnosticsCriticInput input);
    DiagnosticsRecoveryDecision Recover(DiagnosticsCriticDecision critic, PlannerTaskRequest task, DiagnosticsRecoverySettings settings);
}

public interface ILocalModelCriticClient
{
    string AssessJson(DiagnosticsCriticInput input);
}

public sealed class RuleBasedDiagnosticsDecisions(ILocalModelCriticClient? model = null) : IDiagnosticsDecisions
{
    public DiagnosticsCriticDecision Assess(DiagnosticsCriticInput input)
    {
        var plan = input.Planning;
        DiagnosticsCriticDecision Reject(string code, string decision = "reject") => new(plan.Attempt, decision, code, [], null);
        if (!plan.IsValid) return Reject(plan.ErrorCode == "planner.playbook.not_found" ? "evidence.none" : "plan.invalid");
        if (plan.TiedCandidates is { Count: > 1 }) return Reject("evidence.ambiguous", "inconclusive");
        if (input.Steps.Any(s => s.ToolResult.Status == "denied")) return Reject("policy.denied");
        if (input.Steps.Any(s => s.ToolResult.Error is "repo.test.timeout" or "sandbox_timeout")) return Reject("test.timeout");
        if (input.Steps.Any(s => s.ToolResult.Status != "succeeded")) return Reject("tool.failed");
        if (plan.Policy is null || plan.Policy.Requirements.Count == 0) return Reject("evidence.ambiguous", "inconclusive");
        (string InvocationId, RepositoryToolOutput Output)[] outputs;
        try
        {
            outputs = input.Steps.Select(step => (step.ToolResult.InvocationId, Output:
                JsonSerializer.Deserialize<RepositoryToolOutput>(step.ToolResult.OutputJson, RepositoryToolExecutor.JsonOptions)!)).ToArray();
        }
        catch (JsonException) { return Reject("tool.output.invalid"); }
        if (outputs.Any(p => p.Output?.Lines is null)) return Reject("tool.output.invalid");
        if (outputs.Any(p => p.Output is null || p.Output.Snapshot != input.Snapshot)) return Reject("snapshot.mismatch");
        if (outputs.Any(p => p.Output.Truncated)) return Reject("evidence.truncated");
        var refs = new List<DiagnosticEvidenceRef>();
        foreach (var requirement in plan.Policy.Requirements)
        {
            DiagnosticEvidenceRef? found = null;
            foreach (var (id, output) in outputs)
            {
                if (requirement.Kind == "test" && output.Kind == "test" && output.FailureClassification == "test_failed" &&
                    output.Stdout?.Contains(requirement.Contains, StringComparison.Ordinal) == true)
                    found = new(id, "test", null, null, input.Snapshot.ContentHash);
                else if (requirement.Kind == "read" && output.Kind == "read")
                {
                    var line = output.Lines.FirstOrDefault(l => l.Path == requirement.Path && l.Text.Contains(requirement.Contains, StringComparison.Ordinal));
                    if (line is not null) found = new(id, "read", line.Path, line.Line, input.Snapshot.ContentHash);
                }
                if (found is not null) break;
            }
            if (found is null) return Reject(outputs.All(o => o.Output.Lines.Count == 0 && o.Output.Kind != "test") ? "evidence.none" : "evidence.insufficient");
            if (!refs.Contains(found)) refs.Add(found);
        }
        var accepted = new DiagnosticsCriticDecision(plan.Attempt, "accept", "evidence.grounded", refs.AsReadOnly(), plan.Policy.Diagnosis);
        if (model is null) return accepted;
        // A model can only veto a rule-grounded diagnosis. Its recorded assessment never grants authority.
        try
        {
            var json = model.AssessJson(input);
            if (json.Length > 4096) return Reject("model.invalid");
            var assessment = JsonSerializer.Deserialize<ModelCriticAssessment>(json, RepositoryToolExecutor.JsonOptions);
            return assessment?.Decision switch
            {
                "accept" => accepted with { Source = "rules+local-model-v1" },
                "inconclusive" => Reject("model.inconclusive", "inconclusive") with { Source = "rules+local-model-v1" },
                "reject" => Reject("model.rejected") with { Source = "rules+local-model-v1" },
                _ => Reject("model.invalid") with { Source = "rules+local-model-v1" }
            };
        }
        catch (JsonException) { return Reject("model.invalid") with { Source = "rules+local-model-v1" }; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        { return Reject("model.unavailable") with { Source = "rules+local-model-v1" }; }
    }

    public DiagnosticsRecoveryDecision Recover(DiagnosticsCriticDecision critic, PlannerTaskRequest task, DiagnosticsRecoverySettings settings)
    {
        if (settings.MaxRetries is < 0 or > 1) throw new ArgumentException("September permits zero or one retry.");
        var terminal = critic.Decision == "accept" ? "succeeded" : critic.Decision == "inconclusive" ? "inconclusive" :
            critic.ReasonCode is "policy.denied" or "plan.invalid" or "snapshot.mismatch" ? "denied" : "failed";
        if (critic.Decision != "reject" || terminal == "denied")
            return new(critic.Attempt, critic.ReasonCode, "stop", null, terminal);
        if (critic.Attempt > settings.MaxRetries)
            return new(critic.Attempt, critic.ReasonCode, "stop", null, "attempt_limit");
        if (critic.ReasonCode == "test.timeout" && task.Arguments.TryGetValue("profileId", out var profile) &&
            settings.NarrowerProfiles.TryGetValue(profile, out var narrower) && narrower != profile)
        {
            var args = new SortedDictionary<string, string>(task.Arguments.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal)
                { ["profileId"] = narrower };
            return new(critic.Attempt, critic.ReasonCode, "narrower_test", task with { Arguments = args }, null);
        }
        if (critic.ReasonCode == "evidence.none" && settings.AlternateTerms.Count > 0)
            return new(critic.Attempt, critic.ReasonCode, "alternate_retrieval", task with { Terms = settings.AlternateTerms }, null);
        return new(critic.Attempt, critic.ReasonCode, "stop", null, "failed");
    }

    private sealed record ModelCriticAssessment(string Decision);
}
