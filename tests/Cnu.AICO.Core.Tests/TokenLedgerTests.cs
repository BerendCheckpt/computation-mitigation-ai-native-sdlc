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
    public void Crash_with_zeroed_totals_is_flagged_unreliable()
    {
        var r = ClaudeResultParser.Parse("""{"type":"result","subtype":"error_during_execution","is_error":true,"total_cost_usd":0,"usage":{"output_tokens":0}}""");
        Assert.True(r.TotalsUnreliable);
        Assert.False(r.Succeeded);
    }

    [Fact]
    public void Missing_result_message_is_an_error() =>
        Assert.Throws<InvalidDataException>(() => ClaudeResultParser.Parse("""[{"type":"system"}]"""));
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
