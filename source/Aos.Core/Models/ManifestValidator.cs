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

        if (manifest.RoutingDecisions is null || manifest.RoutingDecisions.Count == 0)
        {
            errors.Add("At least one RoutingDecision is required.");
        }
        else
        {
            for (var i = 0; i < manifest.RoutingDecisions.Count; i++)
            {
                var routingDecision = manifest.RoutingDecisions[i];
                if (string.IsNullOrWhiteSpace(routingDecision.TaskClass))
                {
                    errors.Add($"RoutingDecisions[{i}].TaskClass is required.");
                }

                ValidateRouterPolicy(routingDecision.Policy, i, errors);

                if (routingDecision.SelectedCandidate is null)
                {
                    errors.Add($"RoutingDecisions[{i}].SelectedCandidate is required.");
                }
                else
                {
                    ValidateRouterCandidate(
                        routingDecision.SelectedCandidate,
                        $"RoutingDecisions[{i}].SelectedCandidate",
                        errors);
                }

                if (routingDecision.RankedCandidates is null || routingDecision.RankedCandidates.Count == 0)
                {
                    errors.Add($"RoutingDecisions[{i}].RankedCandidates must contain at least one entry.");
                }
                else
                {
                    for (var j = 0; j < routingDecision.RankedCandidates.Count; j++)
                    {
                        ValidateRouterCandidate(
                            routingDecision.RankedCandidates[j].Candidate,
                            $"RoutingDecisions[{i}].RankedCandidates[{j}].Candidate",
                            errors);
                    }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(manifest.EventLog.SchemaVersion))
        {
            errors.Add("EventLog.SchemaVersion is required.");
        }
        else if (!SchemaVersions.IsSupportedEventLogSchemaVersion(manifest.EventLog.SchemaVersion))
        {
            errors.Add(
                $"EventLog.SchemaVersion '{manifest.EventLog.SchemaVersion}' is not supported. Supported version: {SchemaVersions.CurrentEventLogSchemaVersion}.");
        }

        if (manifest.EventLog.RecordCount <= 0)
        {
            errors.Add("EventLog.RecordCount must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(manifest.EventLog.LastChainMac))
        {
            errors.Add("EventLog.LastChainMac is required.");
        }

        if (manifest.CompletedAtUtc is not null &&
            manifest.CompletedAtUtc.Value < manifest.StartedAtUtc)
        {
            errors.Add("CompletedAtUtc cannot be earlier than StartedAtUtc.");
        }

        return errors;
    }

    private static void ValidateRouterPolicy(
        RouterSelectionPolicy? policy,
        int index,
        ICollection<string> errors)
    {
        if (policy is null)
        {
            errors.Add($"RoutingDecisions[{index}].Policy is required.");
            return;
        }

        if (policy.EffectiveConstraints is null)
        {
            errors.Add($"RoutingDecisions[{index}].Policy.EffectiveConstraints is required.");
        }
        else if (policy.EffectiveConstraints.RequiredComplianceTags is null ||
                 policy.EffectiveConstraints.RequiredComplianceTags.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add($"RoutingDecisions[{index}].Policy.EffectiveConstraints.RequiredComplianceTags cannot contain blank entries.");
        }

        if (policy.EffectiveWeights is null)
        {
            errors.Add($"RoutingDecisions[{index}].Policy.EffectiveWeights is required.");
            return;
        }

        var weightTotal = policy.EffectiveWeights.Latency +
                          policy.EffectiveWeights.Cost +
                          policy.EffectiveWeights.Quality +
                          policy.EffectiveWeights.Compliance;
        if (weightTotal <= 0)
        {
            errors.Add($"RoutingDecisions[{index}].Policy.EffectiveWeights must sum to a positive value.");
        }
    }

    private static void ValidateRouterCandidate(
        RouterModelCandidate candidate,
        string path,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(candidate.ModelId))
        {
            errors.Add($"{path}.ModelId is required.");
        }

        if (string.IsNullOrWhiteSpace(candidate.Provider))
        {
            errors.Add($"{path}.Provider is required.");
        }

        if (string.IsNullOrWhiteSpace(candidate.Version))
        {
            errors.Add($"{path}.Version is required.");
        }
    }
}
