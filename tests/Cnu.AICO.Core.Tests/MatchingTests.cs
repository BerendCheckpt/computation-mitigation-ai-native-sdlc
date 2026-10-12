using Cnu.AICO.Core.Matching;
using Cnu.AICO.Core.Specs;

namespace Cnu.AICO.Core.Tests;

public class NormalizerTests
{
    [Fact]
    public void Drops_stop_words_stems_and_rewrites_synonyms()
    {
        var n = new TextNormalizer([new("add", ["create", "scaffold"]), new("endpoint", ["handlers"])]);

        Assert.Equal(["add", "rest", "endpoint", "order"], n.Terms("Please create a REST handlers for the Orders"));
    }

    [Theory]
    [InlineData("endpoints", "endpoint")]
    [InlineData("categories", "category")]
    [InlineData("routing", "rout")]
    [InlineData("status", "status")]
    [InlineData("class", "class")]
    [InlineData("api", "api")]
    public void Stemmer_is_light_and_predictable(string input, string expected) =>
        Assert.Equal(expected, TextNormalizer.Stem(input));

    [Fact]
    public void Bigrams_capture_short_phrases()
    {
        var terms = new TextNormalizer().TermsWithBigrams("minimal api endpoint");
        Assert.Contains("minimal_api", terms);
        Assert.Contains("api_endpoint", terms);
    }

    [Fact]
    public void Cache_key_ignores_case_punctuation_and_stop_words()
    {
        var n = new TextNormalizer();
        Assert.Equal(n.CacheKey("Add an endpoint for Order!"), n.CacheKey("add endpoint order"));
    }
}

public class Bm25Tests
{
    [Fact]
    public void Ranks_the_document_with_rarer_matching_terms_first()
    {
        var index = new Bm25Index();
        index.Add("api", ["add", "endpoint", "route", "api"]);
        index.Add("job", ["add", "background", "job", "schedule"]);

        var results = index.Search(["add", "endpoint"]);

        Assert.Equal("api", results[0].Id);
        Assert.True(results[0].Score > results[1].Score, "shared term 'add' must weigh less than 'endpoint'");
    }

    [Fact]
    public void Repeated_terms_saturate()
    {
        var once = new Bm25Index();
        once.Add("d", ["x", "y", "z", "w"]);
        var many = new Bm25Index();
        many.Add("d", ["x", "x", "x", "x"]);

        var s1 = once.Search(["x"])[0].Score;
        var s4 = many.Search(["x"])[0].Score;

        Assert.True(s4 < 4 * s1);
    }

    [Fact]
    public void No_overlap_returns_nothing()
    {
        var index = new Bm25Index();
        index.Add("api", ["endpoint"]);
        Assert.Empty(index.Search(["kafka"]));
    }
}

public class TfIdfTests
{
    [Fact]
    public void Prefers_terms_specific_to_one_template()
    {
        var top = TfIdfMiner.TopTerms(new Dictionary<string, List<string>>
        {
            ["api"] = ["add", "endpoint", "add", "route", "endpoint"],
            ["job"] = ["add", "job", "schedule", "add"],
        }, top: 2);

        Assert.Equal(["endpoint", "route"], top["api"]);
        Assert.DoesNotContain("add", top["job"].Take(1));
    }
}

public class MatcherTests
{
    static readonly TemplateMatcher Matcher = new(TemplateRegistry.Load(RepoPaths.Templates));

    [Fact]
    public void Crystallized_prompt_routes_deterministically_with_parameters()
    {
        var m = Matcher.Match("Add a minimal API endpoint for entity Order at route /orders in project Shop.Api");

        Assert.Equal(Routes.Deterministic, m.Route);
        Assert.Equal("generate_minimal_api_endpoint", m.ToolName);
        Assert.Equal("Order", m.Parameters["entity"]);
        Assert.Equal("Shop.Api", m.Parameters["project"]);
        Assert.Equal("/orders", m.Parameters["route"]);
    }

    [Fact]
    public void Default_parameters_are_derived()
    {
        var m = Matcher.Match("create crud endpoints for resource category in project Catalog.Api");

        Assert.True(m.IsDeterministic, m.Reason);
        Assert.Equal("Category", m.Parameters["entity"]);
        Assert.Equal("/categories", m.Parameters["route"]);
    }

    [Fact]
    public void Ideation_prompt_routes_to_a_model()
    {
        var m = Matcher.Match("Design a caching strategy for our label renderer");
        Assert.Equal(Routes.Model, m.Route);
    }

    [Fact]
    public void Negative_keyword_vetoes_an_otherwise_matching_prompt()
    {
        var m = Matcher.Match("Explain why the minimal API endpoint for entity Order in project Shop.Api returns 404");
        Assert.Equal(Routes.Model, m.Route);
    }

    [Fact]
    public void Missing_required_parameter_escalates_with_the_candidate()
    {
        var m = Matcher.Match("Add a minimal API endpoint for entity Order");

        Assert.Equal(Routes.Model, m.Route);
        Assert.Equal("minimal-api-endpoint", m.TemplateId);
        Assert.Contains("project", m.MissingParameters);
    }

    [Fact]
    public void Previously_approved_prompt_hits_the_exact_cache()
    {
        var m = Matcher.Match("add a minimal api endpoint for entity customer in project Shop.Api!");

        Assert.True(m.ExactCacheHit);
        Assert.Equal("Customer", m.Parameters["entity"]);
    }

    [Fact]
    public void Non_crystallized_templates_never_route_deterministically()
    {
        var root = RepoPaths.CopyTemplates();
        var spec = Path.Combine(root, "minimal-api-endpoint", "spec.yaml");
        File.WriteAllText(spec, File.ReadAllText(spec).Replace("state: crystallized", "state: candidate"));

        var m = new TemplateMatcher(TemplateRegistry.Load(root))
            .Match("Add a minimal API endpoint for entity Order in project Shop.Api");

        Assert.Equal(Routes.Model, m.Route);
        Assert.Contains("Candidate", m.Reason);
    }
}

/// <summary>Runs evals/routing.yaml: the deterministic routing suite.</summary>
public class RoutingEvalTests
{
    public sealed class EvalFile { public List<EvalCase> Cases { get; set; } = []; }

    public sealed class EvalCase
    {
        public string Prompt { get; set; } = "";
        public string Route { get; set; } = "";
        public string? Template { get; set; }
        public Dictionary<string, string> Parameters { get; set; } = [];
        public override string ToString() => Prompt;
    }

    public static TheoryData<EvalCase> Cases()
    {
        var yaml = File.ReadAllText(Path.Combine(RepoPaths.Root, "evals", "routing.yaml"));
        var file = new YamlDotNet.Serialization.DeserializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
            .Build().Deserialize<EvalFile>(yaml);
        var data = new TheoryData<EvalCase>();
        foreach (var c in file.Cases) data.Add(c);
        return data;
    }

    static readonly TemplateMatcher Matcher = new(TemplateRegistry.Load(RepoPaths.Templates));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Prompt_routes_as_expected(EvalCase c)
    {
        var m = Matcher.Match(c.Prompt);

        Assert.True(c.Route == m.Route, $"Expected {c.Route}, got {m.Route}: {m.Reason}");
        if (c.Template is not null)
            Assert.Equal(c.Template, m.TemplateId);
        foreach (var (k, v) in c.Parameters)
            Assert.Equal(v, m.Parameters.GetValueOrDefault(k));
    }
}
