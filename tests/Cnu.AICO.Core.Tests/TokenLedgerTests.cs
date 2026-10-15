using Cnu.AICO.Core.Specs;
using Cnu.AICO.Core.Telemetry;

namespace Cnu.AICO.Core.Tests;

/// <summary>Parsing follows https://code.claude.com/docs/en/agent-sdk/cost-tracking.</summary>
public class ClaudeResultParserTests
{
    const string Result = """
        {"type":"result","subtype":"success","is_error":false,"duration_ms":41234,"num_turns":7,"session_id":"s1",
         "total_cost_usd":0.2,
         "usage":{"input_tokens":100,"cache_creation_input_tokens":1000,"cache_read_input_tokens":5000,"output_tokens":900},
         "modelUsage":{
           "claude-sonnet-5-5":{"inputTokens":100,"outputTokens":900,"cacheReadInputTokens":5000,"cacheCreationInputTokens":1000,"costUSD":0.15,"costBasis":"list"},
           "claude-haiku-5-5":{"inputTokens":50,"outputTokens":300,"cacheReadInputTokens":0,"cacheCreationInputTokens":200,"costUSD":0.05,"costBasis":"list"}}}
        """;

    [Fact]
    public void Tokens_come_from_modelUsage_which_includes_subagents()
    {
        var r = ClaudeResultParser.Parse(Result);

        // usage alone says 900 output; modelUsage adds the haiku subagent's 300.
        Assert.Equal(new TokenCounts(150, 1200, 5000, 1200), r.Tokens);
        Assert.True(r.FromModelUsage);
        Assert.Equal("claude-sonnet-5-5", r.PrimaryModel);
        Assert.Equal(0.2, r.CostUsdEstimate);
        Assert.Equal("list", r.CostBasis);
        Assert.True(r.Succeeded);
    }

    [Fact]
    public void Falls_back_to_usage_without_modelUsage()
    {
        var r = ClaudeResultParser.Parse("""{"type":"result","subtype":"success","usage":{"input_tokens":1,"output_tokens":2}}""");
        Assert.Equal(new TokenCounts(1, 2, 0, 0), r.Tokens);
        Assert.False(r.FromModelUsage);
    }

    [Fact]
    public void Reads_the_last_result_from_an_execution_file_and_ignores_per_step_placeholders()
    {
        // claude-code-action's execution_file is an array of messages. Assistant messages carry placeholder
        // output_tokens; only the result message has the real total.
        var json = """[{"type":"system"},{"type":"assistant","message":{"id":"m1","usage":{"output_tokens":1}}},""" + Result + "]";

        Assert.Equal(1200, ClaudeResultParser.Parse(json).Tokens.Output);
    }

    [Fact]
    public void Reads_stream_json_lines()
    {
        var lines = "{\"type\":\"system\"}\n" + Result.ReplaceLineEndings(" ") + "\n";
        Assert.Equal(1200, ClaudeResultParser.Parse(lines).Tokens.Output);
    }

    [Fact]
    public void Crash_with_zeroed_totals_after_a_model_call_is_flagged_unreliable()
    {
        var r = ClaudeResultParser.Parse("""{"type":"result","subtype":"error_during_execution","is_error":true,"num_turns":3,"total_cost_usd":0,"usage":{"output_tokens":0}}""");
        Assert.True(r.ModelInvoked);
        Assert.True(r.TotalsUnreliable);
        Assert.False(r.Succeeded);
    }

    [Fact]
    public void Crash_with_partial_tokens_but_zeroed_cost_is_flagged_unreliable()
    {
        var r = ClaudeResultParser.Parse("""{"type":"result","subtype":"error_during_execution","is_error":true,"num_turns":2,"total_cost_usd":0,"usage":{"input_tokens":10,"output_tokens":5}}""");
        Assert.True(r.TotalsUnreliable);
    }

    [Fact]
    public void Zero_tokens_after_a_model_call_are_unreliable()
    {
        var r = ClaudeResultParser.Parse("""{"type":"result","subtype":"success","num_turns":1,"usage":{"input_tokens":0,"output_tokens":0}}""");
        Assert.True(r.TotalsUnreliable);
    }

    [Fact]
    public void A_run_that_invoked_no_model_validly_records_zero_tokens()
    {
        var result = ClaudeResultParser.Parse("""{"type":"result","subtype":"success","num_turns":0,"usage":{"input_tokens":0,"output_tokens":0}}""");
        var crash = ClaudeResultParser.Parse("""{"type":"result","subtype":"error_during_execution","is_error":true,"total_cost_usd":0}""");
        var transcript = ClaudeResultParser.Parse(
            """{"type":"user","sessionId":"s9","message":{"role":"user","content":"/status"}}""");

        Assert.All([result, crash, transcript], r =>
        {
            Assert.False(r.ModelInvoked);
            Assert.False(r.TotalsUnreliable);
            Assert.Equal(0, r.Tokens.Total);
        });
    }

    [Fact]
    public void Successful_run_with_tokens_is_reliable() =>
        Assert.False(ClaudeResultParser.Parse(Result).TotalsUnreliable);

    [Fact]
    public void Missing_result_message_is_an_error() =>
        Assert.Throws<InvalidDataException>(() => ClaudeResultParser.Parse("""[{"type":"system"}]"""));

    [Fact]
    public void Empty_input_is_an_error() =>
        Assert.Throws<InvalidDataException>(() => ClaudeResultParser.Parse("  "));

    // A Claude Code session transcript (~/.claude/projects/<proj>/<session>.jsonl). Message msg_1 was streamed as two
    // content blocks: both lines carry usage, and the last line for an id is the one that counts.
    static readonly string Transcript = string.Join('\n',
        """{"type":"user","sessionId":"sess-1","timestamp":"2026-10-15T09:00:00.000Z","message":{"role":"user","content":"fix it"}}""",
        """{"type":"assistant","sessionId":"sess-1","timestamp":"2026-10-15T09:00:02.000Z","message":{"id":"msg_1","model":"claude-opus-5-5","usage":{"input_tokens":10,"output_tokens":1,"cache_read_input_tokens":1000,"cache_creation_input_tokens":200}}}""",
        """{"type":"assistant","sessionId":"sess-1","timestamp":"2026-10-15T09:00:03.000Z","message":{"id":"msg_1","model":"claude-opus-5-5","usage":{"input_tokens":10,"output_tokens":50,"cache_read_input_tokens":1000,"cache_creation_input_tokens":200}}}""",
        """{"type":"user","sessionId":"sess-1","timestamp":"2026-10-15T09:00:04.000Z","message":{"role":"user","content":[{"type":"tool_result"}]}}""",
        """{"type":"assistant","sessionId":"sess-1","timestamp":"2026-10-15T09:00:10.000Z","message":{"id":"msg_2","model":"claude-opus-5-5","usage":{"input_tokens":5,"output_tokens":20,"cache_read_input_tokens":1200,"cache_creation_input_tokens":0}}}""",
        """{"type":"summary","summary":"Fix"}""");

    static readonly string SubAgentTranscript = string.Join('\n',
        """{"type":"user","isSidechain":true,"sessionId":"sess-1","message":{"role":"user","content":"search"}}""",
        """{"type":"assistant","isSidechain":true,"sessionId":"sess-1","message":{"id":"msg_s1","model":"claude-haiku-5-5","usage":{"input_tokens":3,"output_tokens":7,"cache_read_input_tokens":0,"cache_creation_input_tokens":100}}}""");

    [Fact]
    public void Session_transcript_counts_each_message_id_once_using_its_last_line()
    {
        var r = ClaudeResultParser.Parse(Transcript);

        Assert.Equal(new TokenCounts(15, 70, 2200, 200), r.Tokens);
        Assert.Equal("claude-opus-5-5", r.PrimaryModel);
        Assert.Equal(["claude-opus-5-5"], r.Models);
        Assert.Equal("sess-1", r.SessionId);
        Assert.Equal(2, r.Turns);
        Assert.Equal(10_000, r.DurationMs);
        Assert.Null(r.CostUsdEstimate);
        Assert.True(r.ModelInvoked);
        Assert.False(r.TotalsUnreliable);
        Assert.True(r.Succeeded);
    }

    [Fact]
    public void Main_and_subagent_transcripts_are_summed()
    {
        var r = ClaudeResultParser.Parse([Transcript, SubAgentTranscript]);

        Assert.Equal(new TokenCounts(18, 77, 2200, 300), r.Tokens);
        Assert.Equal("claude-opus-5-5", r.PrimaryModel);
        Assert.Equal(["claude-opus-5-5", "claude-haiku-5-5"], r.Models);
        Assert.Equal(3, r.Turns);
    }

    [Fact]
    public void A_message_that_appears_in_two_transcripts_is_counted_once()
    {
        var r = ClaudeResultParser.Parse([Transcript, Transcript]);
        Assert.Equal(new TokenCounts(15, 70, 2200, 200), r.Tokens);
    }

    [Fact]
    public void Synthetic_assistant_messages_are_not_a_model()
    {
        var text = Transcript + "\n" +
            """{"type":"assistant","sessionId":"sess-1","message":{"id":"msg_x","model":"<synthetic>","usage":{"input_tokens":0,"output_tokens":0}}}""";
        Assert.Equal(["claude-opus-5-5"], ClaudeResultParser.Parse(text).Models);
    }

    [Fact]
    public void A_result_message_wins_over_assistant_lines()
    {
        // stream-json has assistant lines too; when the result is there, its totals are used, not the per-step usage.
        var text = Transcript + "\n" + Result.ReplaceLineEndings(" ");
        Assert.Equal(new TokenCounts(150, 1200, 5000, 1200), ClaudeResultParser.Parse(text).Tokens);
        Assert.Equal(new TokenCounts(150, 1200, 5000, 1200), ClaudeResultParser.Parse([text]).Tokens);
    }

    [Fact]
    public void A_result_and_a_subagent_transcript_are_summed()
    {
        var r = ClaudeResultParser.Parse([Result, SubAgentTranscript]);

        Assert.Equal(new TokenCounts(153, 1207, 5000, 1300), r.Tokens);
        Assert.Equal("claude-sonnet-5-5", r.PrimaryModel);
        Assert.Contains("claude-haiku-5-5", r.Models);
        Assert.Equal(0.2, r.CostUsdEstimate);
        Assert.True(r.Succeeded);
    }

    [Fact]
    public void Several_texts_need_at_least_one() =>
        Assert.Throws<InvalidDataException>(() => ClaudeResultParser.Parse(Array.Empty<string>()));
}

public class TokenLedgerTests
{
    static LedgerRun ModelRun(string id, string? promptKey, int output, DateTimeOffset at, bool error = false, bool? usedTemplate = null) => new()
    {
        Id = id,
        At = at,
        Path = LedgerPaths.Model,
        Source = "stage-6-agent-fix",
        TemplateId = "minimal-api-endpoint",
        PromptKey = promptKey,
        UsedTemplate = usedTemplate,
        Tokens = new TokenCounts(100, output, 50_000, 4_000),
        CostUsdEstimate = 0.40,
        PrimaryModel = "claude-sonnet-5-5",
        Subtype = error ? "error_max_turns" : "success",
        IsError = error,
    };

    static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Baseline_prefers_the_run_with_the_same_prompt()
    {
        var ledger = TokenLedger.Empty();
        ledger.Append(ModelRun("run-a", "key-a", 7000, T0));
        ledger.Append(ModelRun("run-b", "key-b", 6000, T0.AddDays(1)));

        var exact = ledger.FindBaseline("minimal-api-endpoint", "key-a");
        var other = ledger.FindBaseline("minimal-api-endpoint", "key-z");

        Assert.Equal(("run-a", "exact-prompt"), (exact!.Value.Run.Id, exact.Value.Correspondence));
        Assert.Equal(("run-b", "same-template"), (other!.Value.Run.Id, other.Value.Correspondence));
    }

    [Fact]
    public void Failed_runs_and_runs_that_already_used_the_template_are_not_baselines()
    {
        var ledger = TokenLedger.Empty();
        ledger.Append(ModelRun("run-failed", "k", 9000, T0, error: true));
        ledger.Append(ModelRun("run-mixed", "k", 800, T0, usedTemplate: true));

        Assert.Null(ledger.FindBaseline("minimal-api-endpoint", "k"));
    }

    [Fact]
    public void A_zero_token_run_without_a_model_is_reliable_but_never_a_baseline()
    {
        var noModel = LedgerRun.From(ClaudeResultParser.Parse("""{"type":"user","sessionId":"s"}"""), "run-0", LedgerPaths.Model, "local") with
        {
            TemplateId = "minimal-api-endpoint",
            PromptKey = "k",
        };
        var ledger = TokenLedger.Empty();
        ledger.Append(noModel);

        Assert.False(noModel.TotalsUnreliable);
        Assert.Null(ledger.FindBaseline("minimal-api-endpoint", "k"));
    }

    [Fact]
    public void A_deterministic_run_that_invoked_no_model_is_an_accepted_comparison()
    {
        var ledger = TokenLedger.Empty();
        ledger.Record(ModelRun("run-a", "k", 7000, T0));
        var det = LedgerRun.From(ClaudeResultParser.Parse("""{"type":"result","subtype":"success","num_turns":0}"""), "run-d", LedgerPaths.Deterministic, "local") with
        {
            At = T0.AddDays(1),
            TemplateId = "minimal-api-endpoint",
            TemplateVersion = "1.0.0",
            PromptKey = "k",
            UsedTemplate = true,
        };

        var c = ledger.Record(det)!;

        Assert.True(c.Accepted);
        Assert.Equal(7000, c.Saved.Output);
    }

    [Fact]
    public void Recording_a_deterministic_run_compares_it_with_the_baseline_automatically()
    {
        var ledger = TokenLedger.Empty();
        Assert.Null(ledger.Record(ModelRun("run-a", "k", 7000, T0)));   // model path: becomes the baseline

        var det = ModelRun("run-d", "k", 300, T0.AddDays(3)) with
        {
            Path = LedgerPaths.Deterministic,
            UsedTemplate = true,
            TemplateVersion = "1.0.0",
            Tokens = new TokenCounts(40, 300, 12_000, 6_000),
        };
        var c = ledger.Record(det);

        Assert.NotNull(c);
        Assert.True(c.Accepted);
        Assert.Equal(("run-a", "run-d", "exact-prompt"), (c.BaselineRunId, c.DeterministicRunId, c.Correspondence));
        Assert.Equal(6700, c.Saved.Output);
        Assert.Null(ledger.Record(det));                               // re-merging the same fragment adds nothing
        Assert.Single(ledger.Comparisons);
    }

    [Fact]
    public void Deterministic_run_without_a_recorded_baseline_is_logged_without_a_comparison()
    {
        var ledger = TokenLedger.Empty();
        var det = ModelRun("run-d", "k", 300, T0) with { Path = LedgerPaths.Deterministic, UsedTemplate = true, TemplateVersion = "1.0.0" };

        Assert.Null(ledger.Record(det));
        Assert.Single(ledger.Runs);
    }

    [Fact]
    public void Failed_deterministic_run_produces_a_rejected_comparison()
    {
        var ledger = TokenLedger.Empty();
        ledger.Record(ModelRun("run-a", "k", 7000, T0));
        var det = ModelRun("run-d", "k", 9000, T0.AddDays(1), error: true) with { Path = LedgerPaths.Deterministic, UsedTemplate = true, TemplateVersion = "1.0.0" };

        var c = ledger.Record(det)!;

        Assert.False(c.Accepted);
        Assert.Contains("error_max_turns", c.Notes);
    }

    [Fact]
    public void Comparison_subtracts_every_token_class_and_the_cost_estimate()
    {
        var baseline = ModelRun("run-a", "k", 7000, T0);
        var det = baseline with { Id = "run-d", Path = LedgerPaths.Deterministic, Tokens = new TokenCounts(40, 300, 12_000, 6_000), CostUsdEstimate = 0.01 };

        var c = TokenLedger.Compare(baseline, det, "exact-prompt", "1.0.0", accepted: true, null, "sha", null);

        Assert.Equal(new TokenCounts(60, 6700, 38_000, -2_000), c.Saved);
        Assert.Equal(0.39, c.CostUsdSavedEstimate);
    }

    [Fact]
    public void Ledger_roundtrips_through_jsonl_and_ignores_duplicate_ids()
    {
        var path = Path.Combine(RepoPaths.TempDir(), "ledger.jsonl");
        var ledger = TokenLedger.Load(path);
        var run = ModelRun("run-a", "k", 7000, T0);
        ledger.Append(run);
        var det = run with { Id = "run-d", Path = LedgerPaths.Deterministic, Tokens = new TokenCounts(1, 2, 3, 4) };
        ledger.Append(det);
        ledger.Append(TokenLedger.Compare(run, det, "exact-prompt", "1.0.0", true, null, null, null));

        Assert.False(ledger.Append(run));
        var reloaded = TokenLedger.Load(path);
        Assert.Equal(3, reloaded.Entries.Count);
        Assert.Equal(6998, reloaded.Comparisons.Single().Saved.Output);
        Assert.Equal(run, reloaded.Runs.First() with { Models = run.Models });
    }

    [Fact]
    public void Measured_savings_replace_the_estimate_only_for_the_same_template_version()
    {
        var ledger = TokenLedger.Empty();
        var run = ModelRun("run-a", "k", 7000, T0);
        var det = run with { Id = "run-d", Path = LedgerPaths.Deterministic, Tokens = new TokenCounts(1, 300, 0, 0) };
        ledger.Append(TokenLedger.Compare(run, det, "exact-prompt", "1.0.0", true, null, null, null));
        var v1 = new TemplateSpec { Id = "minimal-api-endpoint", Version = "1.0.0", EstimatedOutputTokens = 3500 };
        var v2 = new TemplateSpec { Id = "minimal-api-endpoint", Version = "1.1.0", EstimatedOutputTokens = 3500 };

        Assert.Equal(6700, v1.AvoidedOutputTokens(ledger).Tokens);
        Assert.Equal(BaselineSources.Measured, v1.AvoidedOutputTokens(ledger).Source);
        Assert.Equal(BaselineSources.Estimated, v2.AvoidedOutputTokens(ledger).Source);
    }

    [Fact]
    public void Rejected_comparisons_are_logged_but_never_count_as_measurements()
    {
        var ledger = TokenLedger.Empty();
        var run = ModelRun("run-a", "k", 7000, T0);
        ledger.Append(TokenLedger.Compare(run, run with { Id = "run-d" }, "exact-prompt", "1.0.0", false, "the template tool was not called", null, null));

        Assert.Null(ledger.LatestAcceptedComparison("minimal-api-endpoint", "1.0.0"));
        Assert.Contains("the template tool was not called", ledger.Report());
    }

    [Fact]
    public void Report_is_deterministic_and_states_that_cost_is_an_estimate()
    {
        var ledger = TokenLedger.Empty();
        ledger.Append(ModelRun("run-a", "k", 7000, T0));

        var a = ledger.Report(TemplateRegistry.Load(RepoPaths.Templates));
        var b = ledger.Report(TemplateRegistry.Load(RepoPaths.Templates));

        Assert.Equal(a, b);
        Assert.Contains("client-side estimate", a);
        Assert.Contains("Crystallized but not measured: minimal-api-endpoint@1.0.0 (baseline recorded", a);
    }
}
