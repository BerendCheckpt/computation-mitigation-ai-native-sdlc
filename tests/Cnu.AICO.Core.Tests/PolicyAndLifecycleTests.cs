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

public class SonarFindingsTests
{
    // Shape of GET /api/issues/search (SonarQube 10.x+/2025+: `impacts` plus the legacy `severity`/`type`).
    const string Page = """
        {"paging":{"pageIndex":1,"pageSize":500,"total":4},
         "issues":[
          {"key":"i1","rule":"csharpsquid:S3776","severity":"CRITICAL","component":"cnu-aico:src/A.cs","line":12,
           "message":"Refactor this method to reduce its Cognitive Complexity.","tags":["brain-overload"],
           "impacts":[{"softwareQuality":"MAINTAINABILITY","severity":"HIGH"}]},
          {"key":"i2","rule":"csharpsquid:S101","severity":"MINOR","component":"cnu-aico:src/B.cs","line":3,
           "message":"Rename class 'foo'.","tags":["convention"],
           "impacts":[{"softwareQuality":"MAINTAINABILITY","severity":"LOW"}]},
          {"key":"i3","rule":"csharpsquid:S2259","severity":"MAJOR","component":"cnu-aico:src/C.cs","line":40,
           "message":"'x' is null on at least one execution path.","tags":["cwe"],
           "impacts":[{"softwareQuality":"RELIABILITY","severity":"MEDIUM"}]},
          {"key":"i4","rule":"common-cs:DuplicatedBlocks","severity":"MAJOR","component":"cnu-aico:src/D.cs",
           "message":"2 duplicated blocks of code must be removed.","tags":[],
           "impacts":[{"softwareQuality":"MAINTAINABILITY","severity":"MEDIUM"}]}]}
        """;

    [Fact]
    public void Parses_a_page_and_strips_the_project_from_the_component()
    {
        var page = SonarFindings.ParsePage(Page);

        Assert.Equal(4, page.Total);
        Assert.Equal(4, page.Issues.Count);
        Assert.Equal(("src/A.cs", 12), (page.Issues[0].File, page.Issues[0].Line));
        Assert.Null(page.Issues[3].Line);
    }

    [Theory]
    [InlineData("i1", SonarCategory.Bloat)]       // brain-overload tag
    [InlineData("i2", SonarCategory.Convention)]  // remaining maintainability
    [InlineData("i3", SonarCategory.Bug)]         // reliability impact
    [InlineData("i4", SonarCategory.Bloat)]       // duplication rule
    public void Classifies_bloat_conventions_and_bugs(string key, SonarCategory expected) =>
        Assert.Equal(expected, SonarFindings.Classify(SonarFindings.ParsePage(Page).Issues.Single(i => i.Key == key)));

    [Fact]
    public void Unused_and_redundant_code_is_bloat()
    {
        var issue = new SonarIssue("k", "csharpsquid:S9999", "x.cs", 1, "m", "MINOR", ["unused"], [new("MAINTAINABILITY", "LOW")]);
        Assert.Equal(SonarCategory.Bloat, SonarFindings.Classify(issue));
        Assert.Equal(SonarCategory.Bloat, SonarFindings.Classify(issue with { Tags = ["redundant"] }));
    }

    [Fact]
    public void Blocking_means_a_Blocker_or_High_impact_or_a_legacy_Blocker_or_Critical()
    {
        var issues = SonarFindings.ParsePage(Page).Issues;
        var low = issues.Single(i => i.Key == "i2");

        Assert.True(SonarFindings.IsBlocking(issues.Single(i => i.Key == "i1")));
        Assert.False(SonarFindings.IsBlocking(low));
        Assert.True(SonarFindings.IsBlocking(low with { Severity = "BLOCKER" }));
        Assert.True(SonarFindings.IsBlocking(low with { Impacts = [new("RELIABILITY", "BLOCKER")] }));
        Assert.False(SonarFindings.IsBlocking(low with { Impacts = [new("SECURITY", "HIGH")] }));   // security is out of scope
    }

    [Fact]
    public void Report_groups_findings_and_is_deterministic()
    {
        var issues = SonarFindings.ParsePage(Page).Issues;
        var md = SonarFindings.Report("cnu-aico", issues);

        Assert.Equal(md, SonarFindings.Report("cnu-aico", issues.Reverse().ToList()));
        Assert.True(md.IndexOf("### Bloat", StringComparison.Ordinal) < md.IndexOf("### Conventions", StringComparison.Ordinal));
        Assert.True(md.IndexOf("### Conventions", StringComparison.Ordinal) < md.IndexOf("### Bugs", StringComparison.Ordinal));
        Assert.Contains("`src/A.cs:12` HIGH `csharpsquid:S3776`", md);
        Assert.Contains("`src/D.cs` MEDIUM `common-cs:DuplicatedBlocks`", md);
        Assert.Contains("Blocking (Blocker/High): 1", md);
    }

    [Fact]
    public void Search_path_asks_only_for_maintainability_and_reliability() =>
        Assert.Equal("api/issues/search?componentKeys=cnu-aico&impactSoftwareQualities=MAINTAINABILITY,RELIABILITY&issueStatuses=OPEN,CONFIRMED&ps=500&p=2",
            SonarFindings.SearchPath("cnu-aico", 2));
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
