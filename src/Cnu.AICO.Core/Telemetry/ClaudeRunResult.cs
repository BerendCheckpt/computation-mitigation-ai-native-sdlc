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
/// <item>Local sessions have no result message: their usage is summed from the session transcript(s), and there is no
/// cost estimate. A session that called no model validly records 0 tokens.</item>
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
    /// <summary>Number of distinct assistant messages read from a session transcript (0 for a result message).</summary>
    public int AssistantMessages { get; init; }

    /// <summary>True when a model was called: an assistant message, a non-empty <c>modelUsage</c>, or <c>num_turns</c> &gt; 0.</summary>
    public bool ModelInvoked => AssistantMessages > 0 || Models.Count > 0 || Turns > 0;

    /// <summary>
    /// True when the result cannot be trusted as a measurement: a model was called, yet the totals are zero or the run
    /// crashed with zeroed totals. A run that called no model validly records 0 tokens and is reliable.
    /// </summary>
    public bool TotalsUnreliable =>
        ModelInvoked && (Tokens.Total == 0 || (Subtype == "error_during_execution" && (CostUsdEstimate ?? 0) == 0));

    public bool Succeeded => !IsError && Subtype == "success";
}

public static class ClaudeResultParser
{
    /// <summary>
    /// Accepts the output of <c>claude -p --output-format json</c> (one result object), the
    /// <c>execution_file</c> of claude-code-action (an array of messages), stream-json (one message per line), or a
    /// Claude Code session transcript (<c>~/.claude/projects/&lt;project&gt;/&lt;session&gt;.jsonl</c>).
    /// The last <c>result</c> message wins: in a single-shot call it carries the call's cumulative totals. Without
    /// one, JSON lines are read as a session transcript (see <see cref="Parse(IReadOnlyList{string})"/>).
    /// </summary>
    public static ClaudeRunResult Parse(string text) => Parse([text]);

    /// <summary>
    /// Sums several inputs, typically a main session transcript plus its sub-agent transcripts. Each input that holds
    /// a <c>result</c> message counts as that result. All other inputs are read as session transcripts: the
    /// <c>message.usage</c> of every assistant entry, counting each <c>message.id</c> once (across all inputs) with
    /// the last line seen for it, because a streamed message is written once per content block. The primary model is
    /// the one of the first input that names one.
    /// </summary>
    public static ClaudeRunResult Parse(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0)
            throw new InvalidDataException("No Claude output to read.");

        var parts = new List<ClaudeRunResult>();
        var transcript = new Transcript();
        var transcriptIndex = -1;
        foreach (var text in texts)
        {
            if (FindResult(text) is { } result)
            {
                parts.Add(ParseResult(result));
                continue;
            }
            if (!transcript.Read(text))
                throw new InvalidDataException("No message with \"type\": \"result\" found, and the input is not a session transcript.");
            if (transcriptIndex < 0)
                transcriptIndex = parts.Count;
        }
        if (transcriptIndex >= 0)
            parts.Insert(transcriptIndex, transcript.ToResult());

        return parts.Count == 1 ? parts[0] : Sum(parts);
    }

    static ClaudeRunResult Sum(List<ClaudeRunResult> parts)
    {
        string? costBasis = null;
        foreach (var basis in parts.Select(p => p.CostBasis).Where(b => b is not null))
            costBasis = costBasis is null || costBasis == basis ? basis : "mixed";
        var costs = parts.Where(p => p.CostUsdEstimate is not null).Select(p => p.CostUsdEstimate!.Value).ToList();
        var first = parts[0];
        var failed = parts.FirstOrDefault(p => !p.Succeeded);
        return new ClaudeRunResult(
            Tokens: parts.Aggregate(TokenCounts.Zero, (t, p) => new TokenCounts(
                t.Input + p.Tokens.Input, t.Output + p.Tokens.Output, t.CacheRead + p.Tokens.CacheRead, t.CacheCreation + p.Tokens.CacheCreation)),
            CostUsdEstimate: costs.Count == 0 ? null : costs.Sum(),
            CostBasis: costBasis,
            Models: parts.SelectMany(p => p.Models).Distinct().ToList(),
            PrimaryModel: parts.Select(p => p.PrimaryModel).FirstOrDefault(m => m is not null),
            Subtype: (failed ?? first).Subtype,
            IsError: parts.Any(p => p.IsError),
            Turns: parts.Sum(p => p.Turns),
            DurationMs: parts.Max(p => p.DurationMs),
            SessionId: parts.Select(p => p.SessionId).FirstOrDefault(s => s is not null),
            FromModelUsage: parts.Any(p => p.FromModelUsage))
        {
            AssistantMessages = parts.Sum(p => p.AssistantMessages),
        };
    }

    /// <summary>Assistant entries of one or more session transcripts, deduplicated by <c>message.id</c>.</summary>
    sealed class Transcript
    {
        // Insertion order is kept so the main transcript's models come first.
        readonly Dictionary<string, (string? Model, TokenCounts Tokens)> _messages = new(StringComparer.Ordinal);
        readonly List<string> _order = [];
        string? _sessionId;
        DateTimeOffset? _first;
        DateTimeOffset? _last;
        bool _apiError;

        /// <summary>Reads JSON lines; false when the text is not a transcript (not JSONL, or no session entries).</summary>
        public bool Read(string text)
        {
            var recognised = false;
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { return false; }
                using (doc)
                {
                    var e = doc.RootElement;
                    if (e.ValueKind != JsonValueKind.Object)
                        return false;
                    var type = Str(e, "type");
                    if (type is "user" or "assistant" or "summary" || Str(e, "sessionId") is not null)
                        recognised = true;
                    _sessionId ??= Str(e, "sessionId");
                    if (Str(e, "timestamp") is { } ts && DateTimeOffset.TryParse(ts, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
                    {
                        _first = _first is null || at < _first ? at : _first;
                        _last = _last is null || at > _last ? at : _last;
                    }
                    if (type == "assistant" && e.TryGetProperty("message", out var m) && Str(m, "id") is { } id)
                    {
                        var model = Str(m, "model");
                        if (model is "<synthetic>")
                            model = null;
                        var u = m.TryGetProperty("usage", out var usage) ? usage : default;
                        var tokens = new TokenCounts(Long(u, "input_tokens"), Long(u, "output_tokens"),
                            Long(u, "cache_read_input_tokens"), Long(u, "cache_creation_input_tokens"));
                        if (!_messages.ContainsKey(id))
                            _order.Add(id);
                        _messages[id] = (model, tokens);   // last line for an id wins
                        _apiError = e.TryGetProperty("isApiErrorMessage", out var err) && err.ValueKind == JsonValueKind.True;
                    }
                }
            }
            return recognised;
        }

        public ClaudeRunResult ToResult()
        {
            var tokens = TokenCounts.Zero;
            var perModel = new List<(string Name, long Total)>();
            foreach (var (model, t) in _order.Select(id => _messages[id]))
            {
                tokens = new TokenCounts(tokens.Input + t.Input, tokens.Output + t.Output,
                    tokens.CacheRead + t.CacheRead, tokens.CacheCreation + t.CacheCreation);
                if (model is null)
                    continue;
                var i = perModel.FindIndex(p => p.Name == model);
                if (i < 0)
                    perModel.Add((model, t.Total));
                else
                    perModel[i] = (model, perModel[i].Total + t.Total);
            }
            // A transcript has no result message: a session that ended without an API error counts as a success.
            return new ClaudeRunResult(
                Tokens: tokens,
                CostUsdEstimate: null,
                CostBasis: null,
                Models: perModel.Select(p => p.Name).ToList(),
                PrimaryModel: perModel.Select(p => p.Name).FirstOrDefault(),
                Subtype: _apiError ? "error_during_execution" : "success",
                IsError: _apiError,
                Turns: _messages.Count,
                DurationMs: _first is { } f && _last is { } l ? (long)(l - f).TotalMilliseconds : 0,
                SessionId: _sessionId,
                FromModelUsage: false)
            {
                AssistantMessages = _messages.Count,
            };
        }
    }

    static ClaudeRunResult ParseResult(string result)
    {
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

        // Single object, stream-json or a session transcript (one JSON object per line).
        string? last = null;
        var everyLineIsJson = true;
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
                everyLineIsJson = false;
                last = null;
                break;
            }
        }
        if (everyLineIsJson)
            return last;

        using var whole = JsonDocument.Parse(text);
        return IsResult(whole.RootElement) ? text : null;
    }

    static bool IsResult(JsonElement e) => Str(e, "type") == "result";

    static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    static double? Double(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
