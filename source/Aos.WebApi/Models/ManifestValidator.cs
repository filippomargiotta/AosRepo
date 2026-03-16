namespace Aos.WebApi.Models;

public static class ManifestValidator
{
    public static IReadOnlyList<string> Validate(Manifest manifest)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(manifest.ManifestVersion))
        {
            errors.Add("ManifestVersion is required.");
        }
        else if (!SchemaVersions.IsSupportedManifestVersion(manifest.ManifestVersion))
        {
            errors.Add(
                $"ManifestVersion '{manifest.ManifestVersion}' is not supported. Supported version: {SchemaVersions.CurrentManifestVersion}.");
        }

        if (string.IsNullOrWhiteSpace(manifest.RunId))
        {
            errors.Add("RunId is required.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Seed.SeedId))
        {
            errors.Add("Seed.SeedId is required.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Seed.Algorithm))
        {
            errors.Add("Seed.Algorithm is required.");
        }

        if (string.IsNullOrWhiteSpace(manifest.TimeSource.Mode))
        {
            errors.Add("TimeSource.Mode is required.");
        }
        else if (!manifest.TimeSource.Mode.Equals("record", StringComparison.OrdinalIgnoreCase) &&
                 !manifest.TimeSource.Mode.Equals("replay", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("TimeSource.Mode must be 'record' or 'replay'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.TimeSource.Source))
        {
            errors.Add("TimeSource.Source is required.");
        }

        if (string.IsNullOrWhiteSpace(manifest.TimeSource.ClockId))
        {
            errors.Add("TimeSource.ClockId is required.");
        }

        if (string.IsNullOrWhiteSpace(manifest.TimeSource.Precision))
        {
            errors.Add("TimeSource.Precision is required.");
        }

        if (manifest.Models.Count == 0)
        {
            errors.Add("At least one ModelRef is required.");
        }
        else
        {
            for (var i = 0; i < manifest.Models.Count; i++)
            {
                var model = manifest.Models[i];
                if (string.IsNullOrWhiteSpace(model.ModelId))
                {
                    errors.Add($"Models[{i}].ModelId is required.");
                }

                if (string.IsNullOrWhiteSpace(model.Provider))
                {
                    errors.Add($"Models[{i}].Provider is required.");
                }

                if (string.IsNullOrWhiteSpace(model.Version))
                {
                    errors.Add($"Models[{i}].Version is required.");
                }
            }
        }

        if (manifest.Tools.Count == 0)
        {
            errors.Add("At least one ToolRef is required.");
        }
        else
        {
            for (var i = 0; i < manifest.Tools.Count; i++)
            {
                var tool = manifest.Tools[i];
                if (string.IsNullOrWhiteSpace(tool.ToolId))
                {
                    errors.Add($"Tools[{i}].ToolId is required.");
                }

                if (string.IsNullOrWhiteSpace(tool.Version))
                {
                    errors.Add($"Tools[{i}].Version is required.");
                }
            }
        }

        if (manifest.PolicyDecisions.Count == 0)
        {
            errors.Add("At least one PolicyDecision is required.");
        }
        else
        {
            for (var i = 0; i < manifest.PolicyDecisions.Count; i++)
            {
                var policyDecision = manifest.PolicyDecisions[i];
                if (string.IsNullOrWhiteSpace(policyDecision.PolicyId))
                {
                    errors.Add($"PolicyDecisions[{i}].PolicyId is required.");
                }

                if (string.IsNullOrWhiteSpace(policyDecision.Decision))
                {
                    errors.Add($"PolicyDecisions[{i}].Decision is required.");
                }
                else if (!policyDecision.Decision.Equals("allow", StringComparison.OrdinalIgnoreCase) &&
                         !policyDecision.Decision.Equals("deny", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"PolicyDecisions[{i}].Decision must be 'allow' or 'deny'.");
                }
            }
        }

        if (manifest.CompletedAtUtc is not null &&
            manifest.CompletedAtUtc.Value < manifest.StartedAtUtc)
        {
            errors.Add("CompletedAtUtc cannot be earlier than StartedAtUtc.");
        }

        return errors;
    }
}
