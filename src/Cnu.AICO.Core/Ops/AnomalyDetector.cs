namespace Cnu.AICO.Core.Ops;

/// <summary>Response tiers from <c>ops/bands.md</c>.</summary>
public enum Band
{
    /// <summary>Within 1σ of the rolling baseline: nothing to do.</summary>
    Normal = 0,
    /// <summary>≥ 1σ: log the anomaly.</summary>
    Log = 1,
    /// <summary>≥ 2σ: invoke Claude read-only to diagnose.</summary>
    Diagnose = 2,
    /// <summary>≥ 3σ: Claude opens a PR through the review gate, or a pre-approved runbook runs.</summary>
    Act = 3,
}

public sealed record Detection(string Metric, double Value, double Mean, double StdDev, double Sigma, Band Band);

/// <summary>
/// Deterministic anomaly detection on a metric with a stable rolling baseline (e.g. CI failure rate, post-deploy 5xx rate).
/// No model is involved in deciding *whether* something is wrong — only, at higher bands, in diagnosing it.
/// </summary>
public static class AnomalyDetector
{
    /// <param name="baseline">Recent values, oldest first, excluding <paramref name="current"/>.</param>
    /// <param name="higherIsWorse">True for failure/error rates; false for e.g. throughput.</param>
    public static Detection Evaluate(string metric, IReadOnlyList<double> baseline, double current, bool higherIsWorse = true, int minSamples = 7)
    {
        if (baseline.Count < minSamples)
            return new Detection(metric, current, double.NaN, double.NaN, 0, Band.Normal);

        var mean = baseline.Average();
        var std = Math.Sqrt(baseline.Sum(v => (v - mean) * (v - mean)) / (baseline.Count - 1));
        // A perfectly flat baseline would make any change infinite σ; use a small floor relative to the mean.
        var floor = Math.Max(Math.Abs(mean) * 0.01, 1e-9);
        var sigma = (current - mean) / Math.Max(std, floor);
        if (!higherIsWorse)
            sigma = -sigma;

        var band = sigma >= 3 ? Band.Act : sigma >= 2 ? Band.Diagnose : sigma >= 1 ? Band.Log : Band.Normal;
        return new Detection(metric, current, mean, std, Math.Round(sigma, 3), band);
    }
}
