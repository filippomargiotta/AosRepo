namespace Aos.WebApi.Options;

public sealed class EventLogOptions
{
    public const string SectionName = "EventLog";

    public string Directory { get; set; } = "data";

    public string FileName { get; set; } = "eventlog.jsonl";

    public string ManifestFileName { get; set; } = "manifest.json";

    public string HmacKey { get; set; } = "local-dev-hmac-key";

    public string HmacKeyId { get; set; } = "local-dev";
}
