using System.Text.Json;
using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Aos.WebApi.Services;
using Xunit;

namespace Aos.WebApi.Tests;

public sealed class RepositoryToolsTests
{
    private static RepositorySnapshot Snapshot() => RepositorySnapshot.Import(RepositoryDiagnosticsTests.SnapshotRoot,
        RepositoryDiagnosticsDefaults.Revision, RepositoryDiagnosticsDefaults.CreatedAt);
    private static ToolExecutionRequest Request(RepositorySnapshot snapshot, string action, Dictionary<string, string> arguments)
    {
        arguments["snapshotId"] = snapshot.Reference.SnapshotId;
        arguments["snapshotHash"] = snapshot.Reference.ContentHash;
        return new("run", "invocation", new(action, "1.0"), action, JsonSerializer.Serialize(arguments), "", DateTimeOffset.UnixEpoch);
    }
    private static RepositoryToolExecutor Tools(RepositorySnapshot snapshot) => new(snapshot, [], new NeverRunner());

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("Pricing/../appsettings.json")]
    [InlineData("Pricing\\PriceCalculator.cs")]
    [InlineData("C:/Windows/system.ini")]
    [InlineData("Pricing//PriceCalculator.cs")]
    public void Paths_AreConfined(string path)
    {
        var snapshot = Snapshot();
        var result = Tools(snapshot).Execute(Request(snapshot, "repo.read", new() { ["path"] = path }));
        Assert.Equal("denied", result.Status);
        Assert.Equal("repo.path.denied", result.Error);
    }

    [Fact]
    public void SnapshotIdentityMismatch_IsDenied()
    {
        var snapshot = Snapshot();
        var request = Request(snapshot, "repo.search", new() { ["query"] = "pricing" });
        request = request with { InputJson = request.InputJson.Replace(snapshot.Reference.ContentHash, new string('0', 64)) };
        Assert.Equal("repo.snapshot.mismatch", Tools(snapshot).Execute(request).Error);
    }

    [Theory]
    [InlineData("repo.test", "command", "rm -rf /")]
    [InlineData("repo.test", "profileId", "dotnet test && curl example.com")]
    [InlineData("repo.read", "end", "1000000")]
    [InlineData("repo.search", "limit", "1000000")]
    public void RawCommandsAndUnboundedInputs_AreDenied(string action, string argument, string value)
    {
        var snapshot = Snapshot();
        var input = new Dictionary<string, string> { [argument] = value };
        if (action == "repo.read") input["path"] = "appsettings.json";
        if (action == "repo.search") input["query"] = "pricing";
        Assert.Equal("denied", Tools(snapshot).Execute(Request(snapshot, action, input)).Status);
    }

    [Fact]
    public void Search_IsOrderedAndDeterministicallyBounded()
    {
        var snapshot = Snapshot();
        var result = Tools(snapshot).Execute(Request(snapshot, "repo.search", new() { ["query"] = "SandboxPool", ["limit"] = "1" }));
        var output = JsonSerializer.Deserialize<RepositoryToolOutput>(result.OutputJson, RepositoryToolExecutor.JsonOptions)!;
        Assert.True(output.Truncated);
        Assert.Single(output.Lines);
        Assert.Equal("Configuration/Program.cs", output.Lines[0].Path);
        Assert.Equal(result.OutputJson, Tools(snapshot).Execute(Request(snapshot, "repo.search", new() { ["limit"] = "1", ["query"] = "SandboxPool" })).OutputJson);
    }

    [Fact]
    public void SnapshotOwnsBytes_AfterSourceIsDeleted()
    {
        var dir = GoldenArtifactTestSupport.CreateTempDir("aos-immutable-snapshot");
        File.WriteAllText(Path.Combine(dir, "file.txt"), "original\n");
        var snapshot = RepositorySnapshot.Import(dir, "revision", DateTimeOffset.UnixEpoch);
        File.WriteAllText(Path.Combine(dir, "file.txt"), "mutated\n");
        Directory.Delete(dir, true);
        Assert.Equal("original", Assert.Single(snapshot.Read("file.txt", 1, 1)).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnapshotRejectsFileAndDirectorySymlinks(bool directory)
    {
        var root = GoldenArtifactTestSupport.CreateTempDir("aos-symlink-snapshot");
        var outside = GoldenArtifactTestSupport.CreateTempDir("aos-symlink-outside");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        try
        {
            if (directory) Directory.CreateSymbolicLink(Path.Combine(root, "escape"), outside);
            else File.CreateSymbolicLink(Path.Combine(root, "escape"), Path.Combine(outside, "secret.txt"));
            var ex = Assert.Throws<RepositoryPolicyException>(() => RepositorySnapshot.Import(root, "revision", DateTimeOffset.UnixEpoch));
            Assert.Equal("repo.symlink.denied", ex.ReasonCode);
        }
        finally { Directory.Delete(root, true); Directory.Delete(outside, true); }
    }

    [Fact]
    public void SnapshotRejectsOversizedFile()
    {
        var root = GoldenArtifactTestSupport.CreateTempDir("aos-bounded-snapshot");
        try
        {
            File.WriteAllText(Path.Combine(root, "large.txt"), new string('x', RepositorySnapshot.MaxFileBytes + 1));
            Assert.Equal("repo.snapshot.too_large", Assert.Throws<RepositoryPolicyException>(() =>
                RepositorySnapshot.Import(root, "revision", DateTimeOffset.UnixEpoch)).ReasonCode);
        }
        finally { Directory.Delete(root, true); }
    }

    [ContainerFact]
    public void ContainerSnapshotMount_RejectsWrites()
    {
        using var slot = new ContainerSandboxSlot(new SandboxPoolOptions
            { RepositorySnapshotDirectory = RepositoryDiagnosticsTests.SnapshotRoot }, enableTestCommands: true);
        var result = slot.Execute(new("mount-test", "mount-test:1", new("probe", "1.0"), "probe",
            "{\"sandboxTest\":\"filesystem-write\"}", "", DateTimeOffset.UnixEpoch));
        using var output = JsonDocument.Parse(result.OutputJson);
        Assert.False(output.RootElement.GetProperty("allowed").GetBoolean());
        Assert.False(File.Exists(Path.Combine(RepositoryDiagnosticsTests.SnapshotRoot, "sandbox-write-probe")));
    }

    private sealed class NeverRunner : IRepositoryTestRunner
    {
        public RepositoryTestRun Run(RepositoryTestProfile profile) => throw new InvalidOperationException("Unapproved profile ran.");
    }
}
