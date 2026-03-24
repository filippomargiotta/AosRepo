namespace Aos.WebApi.Models;

public static class SchemaVersions
{
    public const string CurrentManifestVersion = "0.1";
    public const string CurrentEventLogSchemaVersion = "0.2";

    public static bool IsSupportedManifestVersion(string manifestVersion)
    {
        return string.Equals(manifestVersion, CurrentManifestVersion, StringComparison.Ordinal);
    }

    public static bool IsSupportedEventLogSchemaVersion(string schemaVersion)
    {
        return string.Equals(schemaVersion, CurrentEventLogSchemaVersion, StringComparison.Ordinal);
    }
}
