namespace Aos.WebApi.Models;

public sealed record EventLogSchema(
    string SchemaVersion,
    string RunId,
    string Format,
    IReadOnlyList<string> Fields
);
