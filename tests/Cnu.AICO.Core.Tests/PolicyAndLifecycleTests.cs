using Cnu.AICO.Core.Ops;
using Cnu.AICO.Core.Policy;
using Cnu.AICO.Core.Specs;
using Cnu.AICO.Core.Telemetry;

namespace Cnu.AICO.Core.Tests;

public class PolicyTests
{
    static readonly PolicyEvaluator Policy = PolicyEvaluator.Load(RepoPaths.Policy);

    [Fact]
    public void Critical_agent_fix_goes_to_a_human()
    {
        var d = Policy.Evaluate("agent-fix", ["critical"]);

        Assert.False(d.AutonomousAllowed);
        Assert.False(d.ModelAllowed);
        Assert.True(d.RequireHumanReview);
    }

    [Fact]
    public void Non_critical_agent_fix_may_reason()
    {
        var d = Policy.Evaluate("agent-fix", ["bug"]);
        Assert.True(d.ModelAllowed);
        Assert.True(d.AutonomousAllowed);
    }

    [Fact]
    public void Ci_context_is_deterministic_only()
    {
        var d = Policy.Evaluate("ci", []);
        Assert.False(d.ModelAllowed);
        Assert.True(d.AutonomousAllowed);
    }

    [Fact]
    public void Regulated_code_in_a_build_session_requires_human_review()
    {
        var d = Policy.Evaluate("build", ["regulated"]);
        Assert.True(d.ModelAllowed);
        Assert.True(d.RequireHumanReview);
    }

    [Fact]
    public void Missing_context_uses_the_default()
    {
        Assert.Equal("build", Policy.Evaluate(null, null).Context);
    }
}

public class OutcomeRecorderTests
{
    [Fact]
    public void Repeated_failures_demote_a_crystallized_template()
    {
        var registry = TemplateRegistry.Load(RepoPaths.CopyTemplates());
        var recorder = new OutcomeRecorder(registry);
        var failed = new Dictionary<string, bool> { ["build"] = true, ["unitTests"] = false };

        recorder.Record("minimal-api-endpoint", "r1", failed);
        var result = recorder.Record("minimal-api-endpoint", "r2", failed);

        Assert.Equal(TemplateState.Candidate, result.EffectiveState);
        Assert.StartsWith("Demoted", result.Recommendation);
        Assert.True(TemplateRegistry.Load(registry.Root).Find("minimal-api-endpoint")!.Provenance.Demoted, "demotion must persist");
    }

    [Fact]
    public void Passing_run_adds_the_prompt_for_keyword_learning()
    {
        var registry = TemplateRegistry.Load(RepoPaths.CopyTemplates());
        var recorder = new OutcomeRecorder(registry);

        recorder.Record("minimal-api-endpoint", "r1", new Dictionary<string, bool> { ["build"] = true },
            "Introduce REST handlers for model Shipment in service Logistics.Api",
            new Dictionary<string, string> { ["entity"] = "Shipment", ["project"] = "Logistics.Api" });

        var prompts = TemplateRegistry.Load(registry.Root).Find("minimal-api-endpoint")!.Provenance.SuccessfulPrompts;
        Assert.Contains(prompts, p => p.Prompt.Contains("Shipment"));
    }

    [Fact]
    public void Candidate_with_enough_passing_runs_is_eligible_for_promotion()
    {
        var root = RepoPaths.CopyTemplates();
        var spec = Path.Combine(root, "minimal-api-endpoint", "spec.yaml");
        File.WriteAllText(spec, File.ReadAllText(spec).Replace("state: crystallized", "state: candidate"));
        var recorder = new OutcomeRecorder(TemplateRegistry.Load(root));

        var result = recorder.Record("minimal-api-endpoint", "r4", new Dictionary<string, bool> { ["build"] = true });

        Assert.StartsWith("Eligible for promotion", result.Recommendation);
    }
}

public class AnomalyDetectorTests
{
    static readonly double[] Baseline = [0.02, 0.03, 0.025, 0.02, 0.03, 0.028, 0.022, 0.026];

    [Theory]
    [InlineData(0.026, Band.Normal)]
    [InlineData(0.031, Band.Log)]
    [InlineData(0.035, Band.Diagnose)]
    [InlineData(0.08, Band.Act)]
    public void Bands_follow_sigma(double current, Band expected) =>
        Assert.Equal(expected, AnomalyDetector.Evaluate("ci_failure_rate", Baseline, current).Band);

    [Fact]
    public void Too_little_history_never_alerts() =>
        Assert.Equal(Band.Normal, AnomalyDetector.Evaluate("m", [0.1, 0.1], 10).Band);

    // Regression for issue #4: with too little history the detector emitted NaN for mean/σ,
    // which System.Text.Json cannot write, so maintain-detect crashed on every run in a young repo.
    [Fact]
    public void Too_little_history_reports_no_statistics_and_serializes()
    {
        var d = AnomalyDetector.Evaluate("ci_failure_rate", [0.1, 0.2], 0.3);

        Assert.Null(d.Mean);
        Assert.Null(d.StdDev);
        var json = System.Text.Json.JsonSerializer.Serialize(d);
        Assert.Contains("\"Mean\":null", json);
    }

    [Fact]
    public void Non_finite_input_is_rejected_with_a_clear_message()
    {
        var ex = Assert.Throws<ArgumentException>(() => AnomalyDetector.Evaluate("m", Baseline, double.PositiveInfinity));
        Assert.Contains("non-finite", ex.Message);
    }

    [Fact]
    public void Lower_is_worse_metrics_invert_the_sign() =>
        Assert.Equal(Band.Act, AnomalyDetector.Evaluate("throughput", [100, 101, 99, 100, 102, 98, 100, 101], 80, higherIsWorse: false).Band);
}

public class KpiReportTests
{
    [Fact]
    public void Summarizes_avoided_tokens_and_routes()
    {
        var entries = new[]
        {
            new DecisionEntry { Tool = "generate_minimal_api_endpoint", Route = "deterministic", TemplateId = "minimal-api-endpoint", TokensAvoided = 3450, BaselineSource = "estimated" },
            new DecisionEntry { Tool = "match_template", Route = "model" },
            new DecisionEntry { Tool = "template-verify", Route = "deterministic" },
        };

        var md = KpiReport.Build(entries, null, kwhPerMillionTokens: 1, gCo2PerKwh: 300);

        Assert.Contains("| Served deterministically | 2 (67%) |", md);
        Assert.Contains("| AI reviews skipped (regen-verified PRs) | 1 (review tokens not measured) |", md);
        Assert.Contains("| Output tokens avoided, estimated baseline | 3,450 |", md);
        Assert.Contains("| Output tokens avoided, measured baseline | 0 |", md);
        Assert.Contains("3,450", md);
    }
}

public class TemplateValidatorTests
{
    [Fact]
    public void Shipped_templates_are_valid()
    {
        var registry = TemplateRegistry.Load(RepoPaths.Templates);
        Assert.All(registry.All, e => Assert.Empty(TemplateValidator.Validate(e)));
    }

    [Fact]
    public void Crystallized_template_without_evidence_is_rejected()
    {
        var root = RepoPaths.CopyTemplates();
        File.Delete(Path.Combine(root, "minimal-api-endpoint", "provenance.json"));

        var errors = TemplateValidator.Validate(TemplateRegistry.Load(root).Find("minimal-api-endpoint")!);

        Assert.Contains(errors, e => e.Contains("passing runs"));
        Assert.Contains(errors, e => e.Contains("tech-lead"));
    }

    [Fact]
    public void Template_without_shipped_unit_tests_is_rejected()
    {
        var root = RepoPaths.CopyTemplates();
        var spec = Path.Combine(root, "minimal-api-endpoint", "spec.yaml");
        var yaml = File.ReadAllText(spec).Replace("\r\n", "\n");
        yaml = yaml.Replace("  unit:\n    - template: tests/unit/EndpointsTests.cs.scriban\n      path: \"tests/{{ project }}.Tests/Features/{{ entity | plural }}/{{ entity }}EndpointsTests.cs\"\n", "  unit: []\n");
        File.WriteAllText(spec, yaml);

        var errors = TemplateValidator.Validate(TemplateRegistry.Load(root).Find("minimal-api-endpoint")!);

        Assert.Contains(errors, e => e.Contains("unit tests"));
    }
}
