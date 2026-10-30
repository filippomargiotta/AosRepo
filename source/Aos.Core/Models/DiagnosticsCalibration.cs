using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aos.WebApi.Models;

public sealed record ConfidenceObservation(string RunId, double Confidence, bool Correct);
public sealed record ConfidenceBin(double Lower, double Upper, int Count, double MeanConfidence, double Accuracy);
public sealed record ConfidenceThresholds(double? Accept, double? AbstainBelow);
// Stored calibration: the observations' hash binds the thresholds to the dataset they were derived from.
public sealed record ConfidenceCalibration(
    string SchemaVersion, string Formula, string ObservationHash, int Claims, int IncorrectClaims,
    double Brier, double ExpectedCalibrationError, IReadOnlyList<ConfidenceBin> Bins, ConfidenceThresholds Thresholds,
    IReadOnlyList<ConfidenceObservation> Observations, IReadOnlyList<string> Limits);

public static class DiagnosticsConfidence
{
    public const string SchemaVersion = "0.1";
    public const string Formula = "requirementCoverage * (0.6 + 0.4 * termCoverage) * 0.85^(attempts-1) * (testConfirmed ? 1 : 0.95)";
    private const int MinSupport = 3;

    // Runtime-only signals from the recorded plan and outcome; never reads the expected answer.
    public static double Compute(DiagnosticsOutcome outcome, DiagnosticsPlanEvent finalPlan)
    {
        if (outcome.Status != "succeeded" || finalPlan.Policy is null || finalPlan.Policy.Requirements.Count == 0) return 0;
        var requirementCoverage = Math.Min(1.0, (double)outcome.Evidence.Count / finalPlan.Policy.Requirements.Count);
        var terms = finalPlan.Task.Terms.Count;
        var termCoverage = terms == 0 || finalPlan.Selection is null ? 0 : Math.Min(1.0, (double)finalPlan.Selection.MatchedTerms.Count / terms);
        var attempts = Math.Pow(0.85, Math.Max(0, outcome.Attempts - 1));
        var testConfirmed = outcome.Evidence.Any(e => e.Kind == "test") ? 1.0 : 0.95;
        return Math.Round(requirementCoverage * (0.6 + 0.4 * termCoverage) * attempts * testConfirmed, 4, MidpointRounding.ToEven);
    }

    public static ConfidenceCalibration Calibrate(IEnumerable<ConfidenceObservation> observations)
    {
        var sorted = observations.OrderBy(o => o.RunId, StringComparer.Ordinal).ToArray();
        var bins = Enumerable.Range(0, 10).Select(i =>
        {
            var lower = i / 10.0;
            var upper = (i + 1) / 10.0;
            var inBin = sorted.Where(o => o.Confidence >= lower && (o.Confidence < upper || i == 9)).ToArray();
            return new ConfidenceBin(lower, upper, inBin.Length,
                inBin.Length == 0 ? 0 : Math.Round(inBin.Average(o => o.Confidence), 4),
                inBin.Length == 0 ? 0 : Math.Round(inBin.Count(o => o.Correct) / (double)inBin.Length, 4));
        }).Where(b => b.Count > 0).ToArray();
        var brier = sorted.Length == 0 ? 0 : sorted.Average(o => Math.Pow(o.Confidence - (o.Correct ? 1 : 0), 2));
        var ece = sorted.Length == 0 ? 0 : bins.Sum(b => b.Count * Math.Abs(b.MeanConfidence - b.Accuracy)) / sorted.Length;
        // Accept: lowest confidence from which every claim (with enough support) is correct. Abstain: the highest wrong claim.
        double? accept = null;
        foreach (var candidate in sorted.Select(o => o.Confidence).Distinct().Order())
        {
            var above = sorted.Where(o => o.Confidence >= candidate).ToArray();
            if (above.Length >= MinSupport && above.All(o => o.Correct)) { accept = candidate; break; }
        }
        double? abstain = sorted.Any(o => !o.Correct) ? sorted.Where(o => !o.Correct).Max(o => o.Confidence) : null;
        var limits = new List<string>();
        if (sorted.All(o => o.Correct))
            limits.Add("No incorrect claim in the dataset: thresholds are not validated against false accepts and brier/ece measure under-confidence only.");
        if (sorted.Length < 30) limits.Add($"Small dataset ({sorted.Length} claims): thresholds are provisional.");
        return new(SchemaVersion, Formula, Hash(sorted), sorted.Length, sorted.Count(o => !o.Correct), Math.Round(brier, 4), Math.Round(ece, 4),
            bins, new(accept, abstain), sorted, limits);
    }

    public static string Classify(double confidence, ConfidenceThresholds thresholds) =>
        thresholds.Accept is { } accept && confidence >= accept ? "confident" :
        thresholds.AbstainBelow is { } abstain && confidence <= abstain ? "abstain" : "low-confidence";

    // Stored thresholds hold when no incorrect claim would be classified "confident" under them.
    public static IReadOnlyList<string> Verify(ConfidenceThresholds stored, IEnumerable<ConfidenceObservation> observations) =>
        observations.Where(o => !o.Correct && Classify(o.Confidence, stored) == "confident").Select(o => $"{o.RunId}: incorrect claim classified confident").ToArray();

    private static string Hash(IEnumerable<ConfidenceObservation> sorted) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(sorted)))).ToLowerInvariant();
}
