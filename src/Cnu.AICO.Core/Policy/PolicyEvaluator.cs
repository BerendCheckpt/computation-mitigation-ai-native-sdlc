using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cnu.AICO.Core.Policy;

/// <summary>Version-controlled policy (<c>policy.yaml</c>): when a model may reason, and when work must go to a human.</summary>
public sealed class PolicyConfig
{
    public string DefaultContext { get; set; } = "build";

    /// <summary>Contexts in which a model may be invoked at all. Everything else must be deterministic or human.</summary>
    public List<string> ModelAllowedContexts { get; set; } = ["ideation", "feedback", "build", "agent-fix", "diagnose"];

    public List<PolicyRule> Rules { get; set; } = [];
}

public sealed class PolicyRule
{
    public string Name { get; set; } = "";
    public PolicyCondition When { get; set; } = new();

    /// <summary>"human" stops autonomous work (model and tools) and hands it to a human-reviewed PR.</summary>
    public string? Route { get; set; }

    public bool RequireHumanReview { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class PolicyCondition
{
    /// <summary>Any of these labels triggers the rule (empty = any).</summary>
    public List<string> Labels { get; set; } = [];

    /// <summary>Only in these contexts (empty = every context).</summary>
    public List<string> Contexts { get; set; } = [];
}

public sealed record PolicyDecision(
    string Context,
    bool ModelAllowed,
    bool AutonomousAllowed,
    bool RequireHumanReview,
    IReadOnlyList<string> Reasons)
{
    public bool RoutedToHuman => !AutonomousAllowed;
}

public sealed class PolicyEvaluator
{
    static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public PolicyEvaluator(PolicyConfig config) => Config = config;

    public PolicyConfig Config { get; }

    public static PolicyEvaluator Load(string? path) =>
        new(path is not null && File.Exists(path)
            ? Yaml.Deserialize<PolicyConfig>(File.ReadAllText(path)) ?? new PolicyConfig()
            : new PolicyConfig());

    public PolicyDecision Evaluate(string? context, IEnumerable<string>? labels)
    {
        var ctx = string.IsNullOrWhiteSpace(context) ? Config.DefaultContext : context.Trim().ToLowerInvariant();
        var labelSet = (labels ?? []).Select(l => l.Trim().ToLowerInvariant()).Where(l => l.Length > 0).ToHashSet();

        var modelAllowed = Config.ModelAllowedContexts.Contains(ctx, StringComparer.OrdinalIgnoreCase);
        var autonomous = true;
        var review = false;
        var reasons = new List<string>();
        if (!modelAllowed)
            reasons.Add($"Context '{ctx}' runs deterministic flows only; no model reasoning.");

        foreach (var rule in Config.Rules)
        {
            var labelHit = rule.When.Labels.Count == 0 || rule.When.Labels.Any(l => labelSet.Contains(l.ToLowerInvariant()));
            var ctxHit = rule.When.Contexts.Count == 0 || rule.When.Contexts.Contains(ctx, StringComparer.OrdinalIgnoreCase);
            if (!labelHit || !ctxHit)
                continue;

            if (string.Equals(rule.Route, "human", StringComparison.OrdinalIgnoreCase))
            {
                autonomous = false;
                modelAllowed = false;
            }
            review |= rule.RequireHumanReview || !autonomous;
            reasons.Add(string.IsNullOrEmpty(rule.Reason) ? $"Policy rule '{rule.Name}' applies." : rule.Reason);
        }

        return new PolicyDecision(ctx, modelAllowed, autonomous, review, reasons);
    }
}
