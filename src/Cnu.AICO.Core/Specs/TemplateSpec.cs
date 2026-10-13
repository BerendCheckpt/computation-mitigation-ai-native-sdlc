namespace Cnu.AICO.Core.Specs;

/// <summary>Lifecycle of a template. Only <see cref="Crystallized"/> templates are served deterministically.</summary>
public enum TemplateState
{
    Exploratory,
    Candidate,
    Approved,
    Crystallized,
    Deprecated,
}

/// <summary>
/// A template is an executable spec: everything a generator needs to produce code without a model.
/// Deserialized from <c>templates/&lt;id&gt;/spec.yaml</c> and validated against
/// <c>schemas/template-spec.schema.json</c>.
/// </summary>
public sealed class TemplateSpec
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "0.1.0";
    public TemplateState State { get; set; } = TemplateState.Exploratory;
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Owners { get; set; } = [];

    public IntentSpec Intent { get; set; } = new();
    public List<ParameterSpec> Parameters { get; set; } = [];
    public List<string> Requirements { get; set; } = [];
    public List<string> Preconditions { get; set; } = [];
    public List<OutputSpec> Outputs { get; set; } = [];
    public TestsSpec Tests { get; set; } = new();
    public List<string> PostActions { get; set; } = [];

    /// <summary>
    /// Judgement-based guess of the output tokens a model would spend producing this result by hand. Only used until
    /// the token ledger holds an accepted comparison for this template version; KPIs then label the figure "estimated".
    /// </summary>
    public int EstimatedOutputTokens { get; set; } = 2000;

    /// <summary>
    /// Output tokens avoided per deterministic run, and whether that figure is measured (latest accepted comparison in
    /// the token ledger for this template version) or estimated (<see cref="EstimatedOutputTokens"/>).
    /// </summary>
    public AvoidedTokens AvoidedOutputTokens(Telemetry.TokenLedger? ledger = null) =>
        ledger?.LatestAcceptedComparison(Id, Version) is { } c
            ? new AvoidedTokens((int)c.Saved.Output, BaselineSources.Measured, c.Id)
            : new AvoidedTokens(Math.Max(0, EstimatedOutputTokens - Telemetry.DecisionLog.ToolCallTokens), BaselineSources.Estimated, null);

    /// <summary>Optional per-template override of the matcher's minimum confidence (0..1).</summary>
    public double? MatchThreshold { get; set; }

    /// <summary>Absolute path of the template folder; set by the registry, not by YAML.</summary>
    [YamlDotNet.Serialization.YamlIgnore]
    public string Directory { get; set; } = "";

    /// <summary>MCP tool name for this template.</summary>
    public string ToolName => "generate_" + Id.Replace('-', '_');
}

/// <param name="ComparisonId">Ledger comparison the measured figure comes from; null for estimates.</param>
public sealed record AvoidedTokens(int Tokens, string Source, string? ComparisonId);

public static class BaselineSources
{
    public const string Measured = "measured";
    public const string Estimated = "estimated";
}

public sealed class IntentSpec
{
    /// <summary>Canonical keywords that describe the intent the template satisfies.</summary>
    public List<string> Keywords { get; set; } = [];

    /// <summary>canonical → alternatives; alternatives are rewritten to the canonical term before scoring.</summary>
    public Dictionary<string, List<string>> Synonyms { get; set; } = [];

    /// <summary>Any of these in a prompt vetoes the match (e.g. "design", "why").</summary>
    public List<string> Negatives { get; set; } = [];
}

public sealed class ParameterSpec
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "string";
    public string Description { get; set; } = "";
    public bool Required { get; set; }

    /// <summary>Validation regex for the final value.</summary>
    public string? Pattern { get; set; }

    /// <summary>Regex with one capture group used to pull the value out of a natural-language prompt.</summary>
    public string? Extract { get; set; }

    /// <summary>Optional value transform after extraction: pascal | camel | lower | kebab.</summary>
    public string? Transform { get; set; }

    /// <summary>Default value; may be a Scriban expression referencing other parameters, e.g. <c>/{{ entity | string.downcase }}s</c>.</summary>
    public string? Default { get; set; }
}

public sealed class OutputSpec
{
    /// <summary>Scriban template, relative to the template folder.</summary>
    public string Template { get; set; } = "";

    /// <summary>Target path relative to the repository root; Scriban expression.</summary>
    public string Path { get; set; } = "";
}

public sealed class TestsSpec
{
    /// <summary>Unit tests shipped with the template; generated next to the code and verified like code.</summary>
    public List<OutputSpec> Unit { get; set; } = [];

    /// <summary>Integration tests suggested wherever the template is applied; engineers adapt them, so they are not regen-verified.</summary>
    public List<OutputSpec> IntegrationSuggested { get; set; } = [];
}
