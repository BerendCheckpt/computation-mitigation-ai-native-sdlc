using System.Globalization;
using System.Text.Json;

namespace Cnu.AICO.Core.Telemetry;

/// <summary>Arms of a baseline measurement.</summary>
public static class Arms
{
    /// <summary>No template tools: the model writes the code itself.</summary>
    public const string Control = "control";

    /// <summary>The cnu-aico MCP server is available: the model can call the template tool.</summary>
    public const string Template = "template";
}

/// <summary>Usage reported by <c>claude -p --output-format json</c> for one run.</summary>
public sealed record ClaudeUsage(
    int OutputTokens,
    int InputTokens,
    int CacheReadTokens,
    int CacheCreationTokens,
    double? CostUsd,
    int Turns,
    long DurationMs,
    bool IsError);

public sealed record MeasurementRun(
    string Arm,
    int Index,
    ClaudeUsage? Usage,
    bool Accepted,
    bool? UsedTemplate,
    string? Problem);

public sealed record MeasurementSummary(
    string TemplateId,
    string Model,
    string Date,
    int ControlAccepted,
    int TemplateAccepted,
    int? BaselineMedian,
    int? DeterministicMedian,
    string? Problem)
{
    public int? Avoided => BaselineMedian is { } b && DeterministicMedian is { } d ? Math.Max(0, b - d) : null;
    public bool IsUsable => Problem is null;
}

/// <summary>
/// Turns control runs (model writes the code) and template runs (model calls the generate tool) into a measured
/// baseline. Only accepted runs count: a control run must pass the same acceptance checks as a template run, and a
/// template run must actually have used the template — otherwise it measures something else.
/// </summary>
public static class TokenMeasurement
{
    public const int MinAcceptedRuns = 3;

    public static MeasurementSummary Summarize(string templateId, string model, string date, IEnumerable<MeasurementRun> runs)
    {
        var list = runs.ToList();
        var control = list.Where(r => r.Arm == Arms.Control && r.Accepted && r.Usage is not null).Select(r => r.Usage!.OutputTokens).ToList();
        var template = list.Where(r => r.Arm == Arms.Template && r.Accepted && r.UsedTemplate == true && r.Usage is not null)
            .Select(r => r.Usage!.OutputTokens).ToList();

        string? problem = null;
        if (control.Count < MinAcceptedRuns || template.Count < MinAcceptedRuns)
            problem = $"Need at least {MinAcceptedRuns} accepted runs per arm; got control={control.Count}, template={template.Count}.";

        return new MeasurementSummary(templateId, model, date, control.Count, template.Count,
            control.Count == 0 ? null : (int)Math.Round(Median(control)),
            template.Count == 0 ? null : (int)Math.Round(Median(template)),
            problem);
    }

    public static double Median(IReadOnlyCollection<int> values)
    {
        if (values.Count == 0)
            throw new ArgumentException("No values.", nameof(values));
        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>Parses the final result object of <c>claude -p --output-format json</c>. Missing fields read as 0.</summary>
    public static ClaudeUsage ParseClaudeJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // Some versions print an array of messages; the result object is the last one.
        if (root.ValueKind == JsonValueKind.Array)
            root = root.EnumerateArray().Last(e => e.TryGetProperty("type", out var t) && t.GetString() == "result");

        var usage = root.TryGetProperty("usage", out var u) ? u : default;
        int Int(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

        return new ClaudeUsage(
            OutputTokens: Int(usage, "output_tokens"),
            InputTokens: Int(usage, "input_tokens"),
            CacheReadTokens: Int(usage, "cache_read_input_tokens"),
            CacheCreationTokens: Int(usage, "cache_creation_input_tokens"),
            CostUsd: root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null,
            Turns: Int(root, "num_turns"),
            DurationMs: root.TryGetProperty("duration_ms", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt64() : 0,
            IsError: root.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True);
    }

    /// <summary>The block to paste into <c>spec.yaml</c> after the measurement PR is reviewed.</summary>
    public static string ToSpecYaml(MeasurementSummary s, string evidencePath) => string.Create(CultureInfo.InvariantCulture, $"""
        measuredOutputTokens:
          baseline: {s.BaselineMedian}
          deterministic: {s.DeterministicMedian}
          n: {Math.Min(s.ControlAccepted, s.TemplateAccepted)}
          model: {s.Model}
          date: "{s.Date}"
          evidence: {evidencePath}
        """);
}
