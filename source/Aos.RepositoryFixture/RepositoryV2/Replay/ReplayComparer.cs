namespace RepositoryFixture.Replay;

public sealed record RecordedRun(string RunId, string SelectedModel, int EventCount);

public static class ReplayComparer
{
    // Compares the recorded run with the replayed run field by field and reports the first difference.
    public static string? FirstMismatch(RecordedRun recorded, RecordedRun replayed)
    {
        if (recorded.RunId != replayed.RunId) return "manifest.runId";
        if (recorded.SelectedModel != replayed.SelectedModel) return "route.selectedModel";
        if (recorded.EventCount != replayed.EventCount) return "eventLog.eventCount";
        return null;
    }
}
