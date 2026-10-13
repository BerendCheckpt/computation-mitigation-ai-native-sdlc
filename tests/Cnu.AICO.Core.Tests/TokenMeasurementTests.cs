using Cnu.AICO.Core.Specs;
using Cnu.AICO.Core.Telemetry;

namespace Cnu.AICO.Core.Tests;

public class TokenMeasurementTests
{
    static MeasurementRun Run(string arm, int output, bool accepted = true, bool? usedTemplate = null) =>
        new(arm, 0, new ClaudeUsage(output, 1000, 0, 0, 0.01, 3, 1000, false), accepted, usedTemplate ?? (arm == Arms.Template), null);

    [Fact]
    public void Baseline_is_the_median_of_accepted_runs_per_arm()
    {
        var runs = new[]
        {
            Run(Arms.Control, 4000), Run(Arms.Control, 3000), Run(Arms.Control, 5000),
            Run(Arms.Control, 90000, accepted: false),          // failed acceptance: must not count
            Run(Arms.Template, 300), Run(Arms.Template, 200), Run(Arms.Template, 250),
            Run(Arms.Template, 4200, usedTemplate: false),     // model ignored the tool: measures something else
        };

        var s = TokenMeasurement.Summarize("t", "m", "2026-10-13", runs);

        Assert.True(s.IsUsable);
        Assert.Equal(4000, s.BaselineMedian);
        Assert.Equal(250, s.DeterministicMedian);
        Assert.Equal(3750, s.Avoided);
        Assert.Equal(3, s.ControlAccepted);
        Assert.Equal(3, s.TemplateAccepted);
    }

    [Fact]
    public void Too_few_accepted_runs_is_not_a_measurement()
    {
        var s = TokenMeasurement.Summarize("t", "m", "d",
            [Run(Arms.Control, 4000), Run(Arms.Control, 3000), Run(Arms.Template, 200), Run(Arms.Template, 250), Run(Arms.Template, 300)]);

        Assert.False(s.IsUsable);
        Assert.Contains("control=2", s.Problem);
    }

    [Theory]
    [InlineData(new[] { 5, 1, 3 }, 3)]
    [InlineData(new[] { 4, 1, 3, 2 }, 2.5)]
    public void Median(int[] values, double expected) => Assert.Equal(expected, TokenMeasurement.Median(values));

    [Fact]
    public void Parses_claude_print_json_result()
    {
        const string json = """
            {"type":"result","subtype":"success","is_error":false,"duration_ms":41234,"num_turns":7,"result":"done",
             "session_id":"x","total_cost_usd":0.1234,
             "usage":{"input_tokens":120,"cache_creation_input_tokens":9000,"cache_read_input_tokens":40000,"output_tokens":3812}}
            """;

        var u = TokenMeasurement.ParseClaudeJson(json);

        Assert.Equal(3812, u.OutputTokens);
        Assert.Equal(40000, u.CacheReadTokens);
        Assert.Equal(7, u.Turns);
        Assert.Equal(0.1234, u.CostUsd);
        Assert.False(u.IsError);
    }

    [Fact]
    public void Parses_the_result_object_from_a_message_array()
    {
        const string json = """[{"type":"system"},{"type":"result","is_error":true,"usage":{"output_tokens":12}}]""";

        var u = TokenMeasurement.ParseClaudeJson(json);

        Assert.Equal(12, u.OutputTokens);
        Assert.True(u.IsError);
    }

    [Fact]
    public void Spec_snippet_roundtrips_through_the_spec_parser()
    {
        var s = new MeasurementSummary("t", "claude-sonnet-5-5", "2026-10-13", 5, 4, 4000, 250, null);
        var yaml = "id: t\n" + TokenMeasurement.ToSpecYaml(s, "measurements/t-20261013.json");

        var m = TemplateRegistry.ParseSpec(yaml).MeasuredOutputTokens!;

        Assert.Equal(4000, m.Baseline);
        Assert.Equal(250, m.Deterministic);
        Assert.Equal(4, m.N);
        Assert.Equal("2026-10-13", m.Date);
    }
}

public class AvoidedTokensTests
{
    [Fact]
    public void Without_a_measurement_the_estimate_is_used_and_labelled()
    {
        var spec = new TemplateSpec { EstimatedOutputTokens = 3500 };
        Assert.Equal(new AvoidedTokens(3450, BaselineSources.Estimated), spec.AvoidedOutputTokens());
    }

    [Fact]
    public void A_measurement_replaces_the_estimate()
    {
        var spec = new TemplateSpec
        {
            EstimatedOutputTokens = 3500,
            MeasuredOutputTokens = new MeasuredTokens { Baseline = 5200, Deterministic = 400, N = 5, Model = "m", Date = "d", Evidence = "e" },
        };
        Assert.Equal(new AvoidedTokens(4800, BaselineSources.Measured), spec.AvoidedOutputTokens());
    }

    [Fact]
    public void Measurement_without_evidence_file_fails_validation()
    {
        var root = RepoPaths.CopyTemplates();
        var specPath = Path.Combine(root, "minimal-api-endpoint", "spec.yaml");
        File.AppendAllText(specPath, "\nmeasuredOutputTokens:\n  baseline: 5000\n  deterministic: 300\n  n: 2\n  model: m\n  date: \"2026-10-13\"\n  evidence: measurements/missing.json\n");

        var errors = TemplateValidator.Validate(TemplateRegistry.Load(root).Find("minimal-api-endpoint")!);

        Assert.Contains(errors, e => e.Contains("n must be"));
        Assert.Contains(errors, e => e.Contains("evidence"));
    }
}
