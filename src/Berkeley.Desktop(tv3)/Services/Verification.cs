using Berkeley.Contracts;
namespace Berkeley.Desktop.Services;
public static class Verification
{
    public static bool HasMeasurement(double? value) => value.HasValue && double.IsFinite(value.Value) && value.Value >= 0;
    public static bool MeetsThreshold(RoundSummary summary, double seconds) => summary.Outcome == RoundOutcome.Success && HasMeasurement(summary.RemeasuredSpreadMs) && double.IsFinite(seconds) && seconds > 0 && summary.RemeasuredSpreadMs <= seconds * 1000;
}
