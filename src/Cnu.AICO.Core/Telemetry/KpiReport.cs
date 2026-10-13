using System.Globalization;
using System.Text;
using Cnu.AICO.Core.Specs;

namespace Cnu.AICO.Core.Telemetry;

/// <summary>Aggregates decision logs into the weekly mitigation KPI report (markdown).</summary>
public static class KpiReport
{
    /// <param name="kwhPerMillionTokens">Configurable energy factor; publish the value you use next to the report.</param>
    public static string Build(IEnumerable<DecisionEntry> entries, TemplateRegistry? registry, double kwhPerMillionTokens, double gCo2PerKwh, TokenLedger? ledger = null)
    {
        var list = entries.ToList();
        var ci = CultureInfo.InvariantCulture;
        // A request is "served" where the work happens (generate_* or a skipped review), not where it was routed:
        // match_template + generate_* for one request must count once.
        var served = list.Where(e => e.Route == "deterministic" && (e.Tool.StartsWith("generate_", StringComparison.Ordinal) || e.Tool == "template-verify")).ToList();
        var generations = served.Where(e => e.Tool.StartsWith("generate_", StringComparison.Ordinal)).ToList();
        var deterministic = served.Count;
        var model = list.Count(e => e.Route == "model");
        var human = list.Count(e => e.Route == "human");
        var measured = generations.Where(e => e.BaselineSource == BaselineSources.Measured).Sum(e => (long)e.TokensAvoided);
        var estimated = generations.Where(e => e.BaselineSource != BaselineSources.Measured).Sum(e => (long)e.TokensAvoided);
        var kwh = (measured + estimated) / 1_000_000.0 * kwhPerMillionTokens;
        var reviewsSkipped = served.Count(e => e.Tool == "template-verify");
        var outcomes = deterministic + model + human;
        var share = outcomes == 0 ? 0 : (double)deterministic / outcomes;

        var sb = new StringBuilder();
        sb.Append("# Computation mitigation KPIs\n\n");
        sb.Append(ci, $"Period: {(list.Count == 0 ? "no data" : $"{list.Min(e => e.At):yyyy-MM-dd} → {list.Max(e => e.At):yyyy-MM-dd}")}\n\n");
        sb.Append("| KPI | Value |\n|---|---|\n");
        sb.Append(ci, $"| Decisions logged | {list.Count} |\n");
        sb.Append(ci, $"| Served deterministically | {deterministic} ({share * 100:0}%) |\n");
        sb.Append(ci, $"| Escalated to a model | {model} |\n");
        sb.Append(ci, $"| Routed to a human (policy) | {human} |\n");
        sb.Append(ci, $"| AI reviews skipped (regen-verified PRs) | {reviewsSkipped} (review tokens not measured) |\n");
        sb.Append(ci, $"| Output tokens avoided, measured baseline | {measured:N0} |\n");
        sb.Append(ci, $"| Output tokens avoided, estimated baseline | {estimated:N0} |\n");
        sb.Append(ci, $"| Energy avoided (estimate) | {kwh:0.###} kWh ({kwh * gCo2PerKwh / 1000:0.###} kg CO₂e) |\n\n");
        sb.Append("_\"Measured\" means the token ledger holds an accepted comparison for the template version: a deterministic run " +
                  "measured against the recorded non-deterministic run it corresponds to (reports/token-ledger.md). \"Estimated\" means the template still uses its configured guess " +
                  "(estimatedOutputTokens). Avoided tokens are a calibrated counterfactual: no single request can be both served and not served._\n\n");
        sb.Append(ci, $"_Energy factor: {kwhPerMillionTokens} kWh per million output tokens, {gCo2PerKwh} g CO₂e/kWh — always an estimate._\n\n");

        sb.Append("## By template\n\n| Template | Generations | Tokens avoided | Baseline |\n|---|---|---|---|\n");
        foreach (var g in generations.Where(e => e.TemplateId is not null).GroupBy(e => e.TemplateId!).OrderBy(g => g.Key))
        {
            var source = g.Select(e => e.BaselineSource ?? BaselineSources.Estimated).Distinct().Order().ToList();
            sb.Append(ci, $"| {g.Key} | {g.Count()} | {g.Sum(e => (long)e.TokensAvoided):N0} | {string.Join(" + ", source)} |\n");
        }

        if (registry is not null)
        {
            sb.Append("\n## Template lifecycle\n\n| State | Count |\n|---|---|\n");
            foreach (var g in registry.All.GroupBy(e => e.EffectiveState).OrderBy(g => g.Key))
                sb.Append(ci, $"| {g.Key} | {g.Count()} |\n");
            var demoted = registry.All.Where(e => e.Provenance.Demoted).Select(e => e.Spec.Id).ToList();
            sb.Append(ci, $"\nDemoted: {(demoted.Count == 0 ? "none" : string.Join(", ", demoted))}\n");

            sb.Append("\n## Token baselines\n\n| Template | Baseline | Avoided per run | Evidence |\n|---|---|---|---|\n");
            foreach (var e in registry.All.OrderBy(e => e.Spec.Id))
            {
                var avoided = e.Spec.AvoidedOutputTokens(ledger);
                var evidence = avoided.ComparisonId is { } id
                    ? $"ledger comparison `{id}` (reports/token-ledger.md)"
                    : "not measured — estimatedOutputTokens in spec.yaml";
                sb.Append(ci, $"| {e.Spec.Id}@{e.Spec.Version} | {avoided.Source} | {avoided.Tokens:N0} | {evidence} |\n");
            }
        }
        return sb.ToString();
    }
}
