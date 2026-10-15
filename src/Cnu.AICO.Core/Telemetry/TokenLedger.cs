using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cnu.AICO.Core.Specs;

namespace Cnu.AICO.Core.Telemetry;

public static class LedgerPaths
{
    public const string Model = "model";
    public const string Deterministic = "deterministic";
}

/// <summary>One line of <c>ledger/token-usage.jsonl</c>. The file is append-only and committed, so git history is the audit trail.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(LedgerRun), "run")]
[JsonDerivedType(typeof(LedgerComparison), "comparison")]
public abstract record LedgerEntry
{
    public int Schema { get; init; } = 1;
    public required string Id { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Commit the run happened on, so results can be tied to the template/server version in history.</summary>
    public string? GitSha { get; init; }
    public string? WorkflowRun { get; init; }
}

/// <summary>Usage of one real Claude run, as reported by its result message.</summary>
public sealed record LedgerRun : LedgerEntry
{
    /// <summary><see cref="LedgerPaths.Model"/> (the model did the work) or <see cref="LedgerPaths.Deterministic"/> (it called a template tool).</summary>
    public required string Path { get; init; }

    /// <summary>What produced the run: a local agent session (e.g. "local-session", recorded by the SessionEnd hook in its sandbox) or "manual".</summary>
    public required string Source { get; init; }
    public string? Context { get; init; }

    /// <summary>First 300 characters of the request, for humans reading the ledger. Matching uses <see cref="PromptKey"/>.</summary>
    public string? Prompt { get; init; }

    /// <summary>Normalized-prompt hash (same as the matcher's exact cache), used to find the corresponding earlier run.</summary>
    public string? PromptKey { get; init; }

    /// <summary>Template the request belongs to: the one used, or for model runs the matcher's best candidate at record time.</summary>
    public string? TemplateId { get; init; }
    public string? TemplateVersion { get; init; }
    public bool? UsedTemplate { get; init; }

    /// <summary>Whether the run's outcome passed acceptance (null when nobody checked).</summary>
    public bool? Accepted { get; init; }
    public string? Ref { get; init; }

    public required TokenCounts Tokens { get; init; }
    public double? CostUsdEstimate { get; init; }
    public string? CostBasis { get; init; }
    public IReadOnlyList<string> Models { get; init; } = [];
    public string? PrimaryModel { get; init; }
    public string Subtype { get; init; } = "unknown";
    public bool IsError { get; init; }
    public int Turns { get; init; }
    public long DurationMs { get; init; }
    public string? SessionId { get; init; }
    public bool TotalsUnreliable { get; init; }

    /// <summary>A baseline must have used tokens: a run that called no model is reliable, but measures nothing to save.</summary>
    public bool UsableAsBaseline =>
        Path == LedgerPaths.Model && !IsError && Subtype == "success" && !TotalsUnreliable && Tokens.Total > 0 && UsedTemplate != true && Accepted != false;

    public static LedgerRun From(ClaudeRunResult r, string id, string path, string source) => new()
    {
        Id = id,
        Path = path,
        Source = source,
        Tokens = r.Tokens,
        CostUsdEstimate = r.CostUsdEstimate,
        CostBasis = r.CostBasis,
        Models = r.Models,
        PrimaryModel = r.PrimaryModel,
        Subtype = r.Subtype,
        IsError = r.IsError,
        Turns = r.Turns,
        DurationMs = r.DurationMs,
        SessionId = r.SessionId,
        TotalsUnreliable = r.TotalsUnreliable,
    };
}

/// <summary>A deterministic run measured against the earlier non-deterministic run it corresponds to.</summary>
public sealed record LedgerComparison : LedgerEntry
{
    public required string TemplateId { get; init; }
    public required string TemplateVersion { get; init; }
    public required string BaselineRunId { get; init; }
    public required string DeterministicRunId { get; init; }

    /// <summary>"exact-prompt" when both runs had the same normalized prompt, otherwise "same-template".</summary>
    public required string Correspondence { get; init; }
    public string? BaselineModel { get; init; }
    public string? DeterministicModel { get; init; }

    /// <summary>Only accepted comparisons count as measurements (deterministic run used the template and passed acceptance).</summary>
    public bool Accepted { get; init; }

    /// <summary>Baseline minus deterministic. Negative values mean the template path cost more, and are reported as such.</summary>
    public required TokenCounts Saved { get; init; }
    public double? CostUsdSavedEstimate { get; init; }
    public string? Notes { get; init; }
}

/// <summary>Append-only, version-controlled token ledger and the overview generated from it.</summary>
public sealed class TokenLedger
{
    public const string DefaultPath = "ledger/token-usage.jsonl";
    const int PromptPreview = 300;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowOutOfOrderMetadataProperties = true,
    };

    readonly List<LedgerEntry> _entries;

    TokenLedger(string? path, List<LedgerEntry> entries)
    {
        Path = path;
        _entries = entries;
    }

    public string? Path { get; }
    public IReadOnlyList<LedgerEntry> Entries => _entries;
    public IEnumerable<LedgerRun> Runs => _entries.OfType<LedgerRun>();
    public IEnumerable<LedgerComparison> Comparisons => _entries.OfType<LedgerComparison>();

    public static TokenLedger Empty() => new(null, []);

    public static TokenLedger Load(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return new TokenLedger(path, []);
        return new TokenLedger(path, ReadEntries(path).ToList());
    }

    public static IEnumerable<LedgerEntry> ReadEntries(string path)
    {
        var n = 0;
        foreach (var line in File.ReadLines(path))
        {
            n++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            yield return JsonSerializer.Deserialize<LedgerEntry>(line, Json)
                ?? throw new InvalidDataException($"{path}:{n}: empty entry.");
        }
    }

    public static string NewId(string prefix) =>
        $"{prefix}-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}";

    public static string? Preview(string? prompt) =>
        prompt is null ? null : prompt.Length <= PromptPreview ? prompt : prompt[..PromptPreview] + "…";

    /// <summary>Appends to memory and, when the ledger has a path, to the file. Duplicate ids are ignored, so merges are idempotent.</summary>
    public bool Append(LedgerEntry entry)
    {
        if (_entries.Any(e => e.Id == entry.Id))
            return false;
        _entries.Add(entry);
        if (Path is not null)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
            File.AppendAllText(Path, Serialize(entry) + "\n");
        }
        return true;
    }

    public static string Serialize(LedgerEntry entry) => JsonSerializer.Serialize(entry, Json);

    /// <summary>
    /// Records a run from a normal workflow. A model-path run simply becomes available as a baseline. A deterministic-path
    /// run (the model called a template tool) is compared right away against the baseline it corresponds to, so savings
    /// are measured as a side effect of real work: there is no separate measurement job.
    /// Returns the comparison, or null when the run is a model-path run or no baseline exists yet.
    /// </summary>
    public LedgerComparison? Record(LedgerRun run)
    {
        if (_entries.Any(e => e.Id == run.Id))
            return null;
        // Look up the baseline before appending, so a run can never be compared with itself.
        var baseline = run.Path == LedgerPaths.Deterministic && run.TemplateId is not null
            ? FindBaseline(run.TemplateId, run.PromptKey)
            : null;
        Append(run);
        if (baseline is not { } b)
            return null;

        var notes = new List<string>();
        if (run.UsedTemplate != true)
            notes.Add("the template tool was not called");
        if (run.IsError || run.Subtype != "success")
            notes.Add($"run ended with {run.Subtype}");
        if (run.TotalsUnreliable)
            notes.Add("result totals unreliable (zeroed)");
        if (run.Accepted == false)
            notes.Add("outcome not accepted");

        var comparison = Compare(b.Run, run, b.Correspondence, run.TemplateVersion ?? "", accepted: notes.Count == 0,
            notes.Count == 0 ? null : string.Join("; ", notes), run.GitSha, run.WorkflowRun);
        Append(comparison);
        return comparison;
    }

    /// <summary>
    /// The earlier non-deterministic run a deterministic run is compared against: the latest usable model run with the
    /// same normalized prompt, else the latest usable model run for the same template.
    /// </summary>
    public (LedgerRun Run, string Correspondence)? FindBaseline(string templateId, string? promptKey)
    {
        var candidates = Runs.Where(r => r.UsableAsBaseline && r.TemplateId == templateId).OrderByDescending(r => r.At).ToList();
        if (promptKey is not null && candidates.FirstOrDefault(r => r.PromptKey == promptKey) is { } exact)
            return (exact, "exact-prompt");
        return candidates.FirstOrDefault() is { } latest ? (latest, "same-template") : null;
    }

    public LedgerComparison? LatestAcceptedComparison(string templateId, string templateVersion) =>
        Comparisons.Where(c => c.Accepted && c.TemplateId == templateId && c.TemplateVersion == templateVersion)
            .OrderByDescending(c => c.At).FirstOrDefault();

    public static LedgerComparison Compare(LedgerRun baseline, LedgerRun deterministic, string correspondence, string templateVersion,
        bool accepted, string? notes, string? gitSha, string? workflowRun) => new()
        {
            Id = NewId("cmp"),
            GitSha = gitSha,
            WorkflowRun = workflowRun,
            TemplateId = deterministic.TemplateId ?? baseline.TemplateId ?? "",
            TemplateVersion = templateVersion,
            BaselineRunId = baseline.Id,
            DeterministicRunId = deterministic.Id,
            Correspondence = correspondence,
            BaselineModel = baseline.PrimaryModel,
            DeterministicModel = deterministic.PrimaryModel,
            Accepted = accepted,
            Saved = baseline.Tokens - deterministic.Tokens,
            CostUsdSavedEstimate = baseline.CostUsdEstimate is { } b && deterministic.CostUsdEstimate is { } d ? Math.Round(b - d, 6) : null,
            Notes = notes,
        };

    /// <summary>Deterministic markdown overview (same ledger ⇒ same bytes), committed next to the ledger.</summary>
    public string Report(TemplateRegistry? registry = null)
    {
        var ci = CultureInfo.InvariantCulture;
        var runs = Runs.OrderBy(r => r.At).ToList();
        var comparisons = Comparisons.OrderByDescending(c => c.At).ToList();
        var byId = runs.ToDictionary(r => r.Id);
        var sb = new StringBuilder();

        sb.Append("# Token ledger\n\n");
        sb.Append("Generated from `ledger/token-usage.jsonl` by `dotnet run scripts/aico-ci.cs -- ledger report`. Do not edit by hand: " +
                  "the ledger is the source of truth and its git history is the audit trail.\n\n");
        sb.Append("Token counts come from each run's final `result` message (`modelUsage`, which includes subagents). " +
                  "`total_cost_usd` is a client-side estimate from the CLI's price table, not billing data; use the Claude Console " +
                  "or the Usage and Cost API for invoices.\n\n");

        var modelRuns = runs.Where(r => r.Path == LedgerPaths.Model).ToList();
        var detRuns = runs.Where(r => r.Path == LedgerPaths.Deterministic).ToList();
        sb.Append("## Summary\n\n| | Value |\n|---|---|\n");
        sb.Append(ci, $"| Runs recorded | {runs.Count} ({modelRuns.Count} model path, {detRuns.Count} deterministic path) |\n");
        sb.Append(ci, $"| Tokens spent on the model path | {modelRuns.Sum(r => r.Tokens.Total):N0} (output {modelRuns.Sum(r => r.Tokens.Output):N0}) |\n");
        sb.Append(ci, $"| Estimated cost of the model path | ${modelRuns.Sum(r => r.CostUsdEstimate ?? 0):0.00} |\n");
        sb.Append(ci, $"| Comparisons | {comparisons.Count} ({comparisons.Count(c => c.Accepted)} accepted) |\n");
        sb.Append(ci, $"| Period | {(runs.Count == 0 ? "no runs yet" : $"{runs[0].At:yyyy-MM-dd} → {runs[^1].At:yyyy-MM-dd}")} |\n\n");

        sb.Append("## Current measured savings per template\n\nLatest accepted comparison per template version. " +
                  "These figures feed the KPI report as \"measured\".\n\n");
        sb.Append("| Template | Output tokens: model → deterministic | Saved output | Saved total tokens | Saved cost (est.) | Correspondence | Measured |\n|---|---|---|---|---|---|---|\n");
        var current = comparisons.Where(c => c.Accepted).GroupBy(c => (c.TemplateId, c.TemplateVersion)).Select(g => g.First())
            .OrderBy(c => c.TemplateId, StringComparer.Ordinal).ThenBy(c => c.TemplateVersion, StringComparer.Ordinal).ToList();
        if (current.Count == 0)
            sb.Append("| _none yet_ | | | | | | |\n");
        foreach (var c in current)
        {
            var before = byId.GetValueOrDefault(c.BaselineRunId)?.Tokens.Output;
            var after = byId.GetValueOrDefault(c.DeterministicRunId)?.Tokens.Output;
            sb.Append(ci, $"| {c.TemplateId}@{c.TemplateVersion} | {before:N0} → {after:N0} | {c.Saved.Output:N0} | {c.Saved.Total:N0} | {Money(c.CostUsdSavedEstimate)} | {c.Correspondence} | {c.At:yyyy-MM-dd} |\n");
        }

        if (registry is not null)
        {
            var unmeasured = registry.Crystallized.Where(e => current.All(c => c.TemplateId != e.Spec.Id || c.TemplateVersion != e.Spec.Version))
                .Select(e => $"{e.Spec.Id}@{e.Spec.Version}" + (FindBaseline(e.Spec.Id, null) is null ? " (no recorded model run to compare against yet)" : " (baseline recorded: measured automatically at its first deterministic run)"))
                .ToList();
            if (unmeasured.Count > 0)
                sb.Append("\nCrystallized but not measured: ").Append(string.Join("; ", unmeasured)).Append(". KPIs use the template's `estimatedOutputTokens` for these, labelled \"estimated\".\n");
        }

        sb.Append("\n## Comparison history\n\n| Date | Template | Baseline run | Deterministic run | Saved output | Saved total | Accepted | Notes |\n|---|---|---|---|---|---|---|---|\n");
        if (comparisons.Count == 0)
            sb.Append("| _none yet_ | | | | | | | |\n");
        foreach (var c in comparisons)
            sb.Append(ci, $"| {c.At:yyyy-MM-dd} | {c.TemplateId}@{c.TemplateVersion} | `{c.BaselineRunId}` | `{c.DeterministicRunId}` | {c.Saved.Output:N0} | {c.Saved.Total:N0} | {(c.Accepted ? "yes" : "no")} | {Cell(c.Notes)} |\n");

        sb.Append("\n## Runs by source\n\n| Source | Path | Runs | Output | Input | Cache read | Cache write | Cost (est.) |\n|---|---|---|---|---|---|---|---|\n");
        foreach (var g in runs.GroupBy(r => (r.Source, r.Path)).OrderBy(g => g.Key.Source, StringComparer.Ordinal).ThenBy(g => g.Key.Path, StringComparer.Ordinal))
            sb.Append(ci, $"| {g.Key.Source} | {g.Key.Path} | {g.Count()} | {g.Sum(r => r.Tokens.Output):N0} | {g.Sum(r => r.Tokens.Input):N0} | {g.Sum(r => r.Tokens.CacheRead):N0} | {g.Sum(r => r.Tokens.CacheCreation):N0} | {Money(g.Sum(r => r.CostUsdEstimate ?? 0))} |\n");
        if (runs.Count == 0)
            sb.Append("| _none yet_ | | | | | | | |\n");

        sb.Append("\n## Recent runs\n\n| Date | Run | Source | Path | Template | Model | Output | Total tokens | Cost (est.) | Result |\n|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (var r in runs.AsEnumerable().Reverse().Take(50))
            sb.Append(ci, $"| {r.At:yyyy-MM-dd} | `{r.Id}` | {r.Source} | {r.Path} | {r.TemplateId ?? "–"} | {r.PrimaryModel ?? "–"} | {r.Tokens.Output:N0} | {r.Tokens.Total:N0} | {Money(r.CostUsdEstimate)} | {r.Subtype}{(r.TotalsUnreliable ? " (totals unreliable)" : "")} |\n");
        if (runs.Count == 0)
            sb.Append("| _none yet_ | | | | | | | | | |\n");

        return sb.ToString();
    }

    static string Money(double? usd) => usd is { } v ? v.ToString("$0.0000", CultureInfo.InvariantCulture) : "–";

    static string Cell(string? s) => string.IsNullOrEmpty(s) ? "" : s.Replace("|", "\\|").Replace("\n", " ");
}
