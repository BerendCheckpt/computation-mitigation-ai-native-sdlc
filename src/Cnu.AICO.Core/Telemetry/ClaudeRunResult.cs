using System.Text.Json;

namespace Cnu.AICO.Core.Telemetry;

/// <summary>Token counts for one Claude run (whole agent tree, including subagents).</summary>
public sealed record TokenCounts(long Input, long Output, long CacheRead, long CacheCreation)
{
    public static readonly TokenCounts Zero = new(0, 0, 0, 0);

    public long Total => Input + Output + CacheRead + CacheCreation;

    public static TokenCounts operator -(TokenCounts a, TokenCounts b) =>
        new(a.Input - b.Input, a.Output - b.Output, a.CacheRead - b.CacheRead, a.CacheCreation - b.CacheCreation);
}

/// <summary>
/// What a Claude run reported about its own usage, read from the final <c>result</c> message as the Agent SDK
/// cost-tracking guide prescribes (https://code.claude.com/docs/en/agent-sdk/cost-tracking):
/// <list type="bullet">
/// <item>Tokens come from <c>modelUsage</c>, which includes subagents; <c>usage</c> only covers the top-level loop
/// and is the fallback when <c>modelUsage</c> is absent.</item>
/// <item>Output tokens are only read from the result message: per-step assistant <c>output_tokens</c> are placeholders.</item>
/// <item><c>total_cost_usd</c> is a client-side estimate, never billing data.</item>
/// <item>An <c>error_during_execution</c> result after a crash may carry zeroed totals; such runs are flagged and never
/// used as a comparison baseline.</item>
/// </list>
/// </summary>
public sealed record ClaudeRunResult(
    TokenCounts Tokens,
    double? CostUsdEstimate,
    string? CostBasis,
    IReadOnlyList<string> Models,
    string? PrimaryModel,
    string Subtype,
    bool IsError,
    int Turns,
    long DurationMs,
    string? SessionId,
    bool FromModelUsage)
{
    /// <summary>True when the result cannot be trusted as a measurement (crash with zeroed totals, or no tokens at all).</summary>
    public bool TotalsUnreliable => Tokens.Total == 0 || (Subtype == "error_during_execution" && (CostUsdEstimate ?? 0) == 0);

    public bool Succeeded => !IsError && Subtype == "success";
}

public static class ClaudeResultParser
{
    /// <summary>
    /// Accepts the output of <c>claude -p --output-format json</c> (one result object), the
    /// <c>execution_file</c> of claude-code-action (an array of messages) or stream-json (one message per line).
    /// The last <c>result</c> message wins: in a single-shot call it carries the call's cumulative totals.
    /// </summary>
    public static ClaudeRunResult Parse(string text)
    {
        var result = FindResult(text) ?? throw new InvalidDataException("No message with \"type\": \"result\" found.");
        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        var models = new List<(string Name, double Cost)>();
        TokenCounts tokens = TokenCounts.Zero;
        string? costBasis = null;
        var fromModelUsage = false;

        if (root.TryGetProperty("modelUsage", out var mu) && mu.ValueKind == JsonValueKind.Object && mu.EnumerateObject().Any())
        {
            fromModelUsage = true;
            foreach (var m in mu.EnumerateObject())
            {
                var v = m.Value;
                tokens = new TokenCounts(
                    tokens.Input + Long(v, "inputTokens"),
                    tokens.Output + Long(v, "outputTokens"),
                    tokens.CacheRead + Long(v, "cacheReadInputTokens"),
                    tokens.CacheCreation + Long(v, "cacheCreationInputTokens"));
                var cost = Double(v, "costUSD") ?? 0;
                models.Add((m.Name, cost));
                var basis = Str(v, "costBasis");
                costBasis = costBasis is null || costBasis == basis ? basis : "mixed";
            }
        }
        else if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            tokens = new TokenCounts(Long(u, "input_tokens"), Long(u, "output_tokens"),
                Long(u, "cache_read_input_tokens"), Long(u, "cache_creation_input_tokens"));
        }

        return new ClaudeRunResult(
            Tokens: tokens,
            CostUsdEstimate: Double(root, "total_cost_usd"),
            CostBasis: costBasis,
            Models: models.Select(m => m.Name).ToList(),
            PrimaryModel: models.OrderByDescending(m => m.Cost).Select(m => m.Name).FirstOrDefault(),
            Subtype: Str(root, "subtype") ?? "unknown",
            IsError: root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True,
            Turns: (int)Long(root, "num_turns"),
            DurationMs: Long(root, "duration_ms"),
            SessionId: Str(root, "session_id"),
            FromModelUsage: fromModelUsage);
    }

    static string? FindResult(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return null;

        if (text[0] == '[')
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.EnumerateArray().LastOrDefault(IsResult) is { ValueKind: JsonValueKind.Object } r ? r.GetRawText() : null;
        }

        // Single object, or stream-json (one JSON object per line).
        string? last = null;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (IsResult(doc.RootElement))
                    last = line;
            }
            catch (JsonException)
            {
                // A pretty-printed single object spans lines: fall back to parsing the whole text below.
                last = null;
                break;
            }
        }
        if (last is not null)
            return last;

        using var whole = JsonDocument.Parse(text);
        return IsResult(whole.RootElement) ? text : null;
    }

    static bool IsResult(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("type", out var t) && t.GetString() == "result";

    static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    static double? Double(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
