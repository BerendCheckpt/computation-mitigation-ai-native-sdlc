using System.Globalization;
using System.Text;
using Cnu.AICO.Core.Specs;

namespace Cnu.AICO.Core.Telemetry;

/// <summary>Aggregates decision logs into the weekly mitigation KPI report (markdown).</summary>
public static class KpiReport
{
    /// <param name="kwhPerMillionTokens">Configurable energy factor; publish the value you use next to the report.</param>
    public static string Build(IEnumerable<DecisionEntry> entries, TemplateRegistry? registry, double kwhPerMillionTokens, double gCo2PerKwh)
    {
        var list = entries.ToList();
        var ci = CultureInfo.InvariantCulture;
        // A request is "served" where the work happens (generate_* or a skipped review), not where it was routed:
        // match_template + generate_* for one request must count once.
        var served = list.Where(e => e.Route == "deterministic" && (e.Tool.StartsWith("generate_", StringComparison.Ordinal) || e.Tool == "template-verify")).ToList();
        var deterministic = served.Count;
        var model = list.Count(e => e.Route == "model");
        var human = list.Count(e => e.Route == "human");
        var tokens = served.Sum(e => (long)e.EstimatedTokensAvoided);
        var kwh = tokens / 1_000_000.0 * kwhPerMillionTokens;
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
        sb.Append(ci, $"| AI reviews skipped (regen-verified PRs) | {reviewsSkipped} |\n");
        sb.Append(ci, $"| Estimated output tokens avoided | {tokens:N0} |\n");
        sb.Append(ci, $"| Estimated energy avoided | {kwh:0.###} kWh ({kwh * gCo2PerKwh / 1000:0.###} kg CO₂e) |\n\n");
        sb.Append(ci, $"_Energy factor: {kwhPerMillionTokens} kWh per million output tokens, {gCo2PerKwh} g CO₂e/kWh — estimates, not measurements._\n\n");

        sb.Append("## By template\n\n| Template | Deterministic | Tokens avoided |\n|---|---|---|\n");
        foreach (var g in served.Where(e => e.TemplateId is not null).GroupBy(e => e.TemplateId!).OrderBy(g => g.Key))
            sb.Append(ci, $"| {g.Key} | {g.Count()} | {g.Sum(e => (long)e.EstimatedTokensAvoided):N0} |\n");

        if (registry is not null)
        {
            sb.Append("\n## Template lifecycle\n\n| State | Count |\n|---|---|\n");
            foreach (var g in registry.All.GroupBy(e => e.EffectiveState).OrderBy(g => g.Key))
                sb.Append(ci, $"| {g.Key} | {g.Count()} |\n");
            var demoted = registry.All.Where(e => e.Provenance.Demoted).Select(e => e.Spec.Id).ToList();
            sb.Append(ci, $"\nDemoted: {(demoted.Count == 0 ? "none" : string.Join(", ", demoted))}\n");
        }
        return sb.ToString();
    }
}
