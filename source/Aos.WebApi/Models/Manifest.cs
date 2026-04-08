namespace Aos.WebApi.Models;

public sealed record Manifest(
    string ManifestVersion,
    string RunId,
    SeedInfo Seed,
    TimeSourceInfo TimeSource,
    IReadOnlyList<ModelRef> Models,
    IReadOnlyList<ToolRef> Tools,
    IReadOnlyList<PolicyDecision> PolicyDecisions,
    EventLogSummary EventLog,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc
);

public sealed record EventLogSummary(
    string SchemaVersion,
    int RecordCount,
    string LastChainMac
);

public sealed record SeedInfo(
    string SeedId,
    string Algorithm,
    long Value,
    string? Derivation
);

public sealed record TimeSourceInfo(
    string Mode,
    string Source,
    string ClockId,
    string Precision,
    string? Notes
);

public sealed record ModelRef(
    string ModelId,
    string Provider,
    string Version
);

public sealed record ToolRef(
    string ToolId,
    string Version
);

public sealed record PolicyDecision(
    string PolicyId,
    string Decision,
    string? Reason
);

public sealed record ManifestRecord(
    Manifest Manifest,
    ManifestIntegrity Integrity
);

public sealed record ManifestIntegrity(
    string Algorithm,
    string KeyId,
    string ManifestMac
);
