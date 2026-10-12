using System.Globalization;
using System.Text.RegularExpressions;
using Cnu.AICO.Core.Generation;
using Cnu.AICO.Core.Specs;

namespace Cnu.AICO.Core.Matching;

public sealed record ResolvedParameters(
    Dictionary<string, string> Values,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Errors)
{
    public bool IsComplete => Missing.Count == 0 && Errors.Count == 0;
}

/// <summary>
/// Fills a template's parameter contract: values extracted from a prompt or supplied by a caller,
/// then transforms, then defaults (which may reference other parameters), then validation.
/// The same contract is exposed as the MCP tool's input schema.
/// </summary>
public static class ParameterResolver
{
    static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Pulls parameter values out of free text using each parameter's <c>extract</c> regex.</summary>
    public static Dictionary<string, string> Extract(TemplateSpec spec, string prompt)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in spec.Parameters)
        {
            if (string.IsNullOrEmpty(p.Extract))
                continue;
            var m = Regex.Match(prompt, p.Extract, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            if (m.Success && m.Groups.Count > 1 && m.Groups[1].Success)
                values[p.Name] = m.Groups[1].Value;
        }
        return values;
    }

    public static ResolvedParameters Resolve(TemplateSpec spec, IReadOnlyDictionary<string, string> supplied)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new List<string>();

        foreach (var (name, value) in supplied)
        {
            var p = spec.Parameters.FirstOrDefault(x => x.Name == name);
            if (p is null)
            {
                errors.Add($"Unknown parameter '{name}'.");
                continue;
            }
            values[name] = ApplyTransform(value.Trim(), p.Transform);
        }

        // Defaults may reference other parameters, so resolve them after the explicit values.
        foreach (var p in spec.Parameters.Where(p => !values.ContainsKey(p.Name) && p.Default is not null))
            values[p.Name] = TemplateGenerator.RenderString(p.Default!, values);

        var missing = spec.Parameters.Where(p => p.Required && string.IsNullOrWhiteSpace(values.GetValueOrDefault(p.Name)))
            .Select(p => p.Name).ToList();

        foreach (var p in spec.Parameters)
        {
            if (p.Pattern is null || !values.TryGetValue(p.Name, out var v) || v.Length == 0)
                continue;
            if (!Regex.IsMatch(v, p.Pattern, RegexOptions.CultureInvariant, RegexTimeout))
                errors.Add($"Parameter '{p.Name}' value '{v}' does not match {p.Pattern}.");
        }

        return new ResolvedParameters(values, missing, errors);
    }

    static string ApplyTransform(string value, string? transform) => transform?.ToLowerInvariant() switch
    {
        "pascal" => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..],
        "camel" => value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..],
        "lower" => value.ToLower(CultureInfo.InvariantCulture),
        "kebab" => Regex.Replace(value, "(?<!^)([A-Z])", "-$1", RegexOptions.None, RegexTimeout).ToLowerInvariant(),
        _ => value,
    };
}
