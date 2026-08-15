using Aos.WebApi.Models;
using Aos.WebApi.Services;
using Xunit;

namespace Aos.WebApi.Tests;

public sealed class PlaybookRetrievalTests
{
    [Fact]
    public void Retrieve_RanksByTermOverlapThenLexicalIdentity()
    {
        var retriever = CreateRetriever(
            CreatePlaybook("gamma", "1", "workflow.plan", ["audit", "replay"]),
            CreatePlaybook("beta", "1", "workflow.plan", ["replay"]),
            CreatePlaybook("alpha", "1", "workflow.plan", ["audit", "replay"]));

        var matches = retriever.Retrieve(new PlaybookRetrievalRequest(
            TaskClass: "workflow.plan",
            Terms: ["REPLAY", "audit", "audit"]));

        Assert.Equal(["alpha", "gamma", "beta"], matches.Select(match => match.Playbook.PlaybookId));
        Assert.Equal([2, 2, 1], matches.Select(match => match.Score));
        Assert.Equal(["audit", "replay"], matches[0].MatchedTerms);
    }

    [Fact]
    public void Retrieve_IsIndependentOfPlaybookAndQueryTermOrder()
    {
        var playbooks = new[]
        {
            CreatePlaybook("gamma", "1", "workflow.plan", ["audit", "replay"]),
            CreatePlaybook("beta", "1", "workflow.plan", ["replay"]),
            CreatePlaybook("alpha", "1", "workflow.plan", ["audit", "replay"])
        };
        var forward = CreateRetriever(playbooks).Retrieve(
            new PlaybookRetrievalRequest("workflow.plan", ["audit", "replay"]));
        var reversed = CreateRetriever(playbooks.Reverse().ToArray()).Retrieve(
            new PlaybookRetrievalRequest("workflow.plan", ["replay", "audit"]));

        Assert.Equal(
            forward.Select(IdentityAndScore),
            reversed.Select(IdentityAndScore));
    }

    [Fact]
    public void Retrieve_FiltersTaskClassAndZeroOverlapAndHonorsLimit()
    {
        var retriever = CreateRetriever(
            CreatePlaybook("alpha", "1", "workflow.plan", ["audit", "replay"]),
            CreatePlaybook("beta", "1", "workflow.plan", ["replay"]),
            CreatePlaybook("gamma", "1", "workflow.plan", ["unrelated"]),
            CreatePlaybook("other-task", "1", "workflow.other", ["audit", "replay"]));

        var matches = retriever.Retrieve(new PlaybookRetrievalRequest(
            TaskClass: "workflow.plan",
            Terms: ["audit", "replay"],
            MaxResults: 2));

        Assert.Equal(["alpha", "beta"], matches.Select(match => match.Playbook.PlaybookId));
    }

    [Fact]
    public void Store_SnapshotsAndNormalizesRetrievalTerms()
    {
        var mutableTerms = new List<string> { " Replay ", "AUDIT", "replay" };
        var playbook = CreatePlaybook("alpha", "1", "workflow.plan", mutableTerms);
        var store = new InMemoryPlaybookStore([playbook]);
        mutableTerms.Clear();

        var stored = Assert.Single(store.ListByTaskClass("workflow.plan"));

        Assert.Equal(["audit", "replay"], stored.RetrievalTerms);
    }

    [Fact]
    public void Store_DuplicateIdentityAndVersion_Throws()
    {
        var playbook = CreatePlaybook("alpha", "1", "workflow.plan", ["audit"]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new InMemoryPlaybookStore([playbook, playbook]));

        Assert.Equal("Playbooks contain duplicate id/version entries: alpha/1.", exception.Message);
    }

    [Fact]
    public void Store_UnsupportedSchemaVersion_Throws()
    {
        var playbook = CreatePlaybook("alpha", "1", "workflow.plan", ["audit"]) with
        {
            SchemaVersion = "9.9"
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new InMemoryPlaybookStore([playbook]));

        Assert.Contains("schema version '9.9' is not supported", exception.Message, StringComparison.Ordinal);
    }

    private static DeterministicPlaybookRetriever CreateRetriever(params PlannerPlaybook[] playbooks) =>
        new(new InMemoryPlaybookStore(playbooks));

    private static PlannerPlaybook CreatePlaybook(
        string id,
        string version,
        string taskClass,
        IReadOnlyList<string> terms) =>
        new(
            SchemaVersion: PlannerSchemaVersions.CurrentPlaybookVersion,
            PlaybookId: id,
            Version: version,
            TaskClass: taskClass,
            Description: $"Playbook {id}",
            RetrievalTerms: terms,
            Steps:
            [
                new PlannerPlaybookStep(
                    ActionId: "echo.execute",
                    ArgumentTemplates: new Dictionary<string, string> { ["input"] = "{{task.input}}" })
            ]);

    private static string IdentityAndScore(PlaybookMatch match) =>
        $"{match.Playbook.PlaybookId}/{match.Playbook.Version}:{match.Score}";
}
