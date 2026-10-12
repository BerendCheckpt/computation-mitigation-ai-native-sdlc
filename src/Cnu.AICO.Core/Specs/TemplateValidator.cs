using System.Text.RegularExpressions;
using Cnu.AICO.Core.Generation;

namespace Cnu.AICO.Core.Specs;

/// <summary>
/// CI gate for template specs. Mirrors <c>schemas/template-spec.schema.json</c> (which editors use for
/// authoring) and adds the checks a schema cannot express: referenced files exist, regexes compile,
/// paths render, and a crystallized template carries the evidence and approvals that justify it.
/// </summary>
public static partial class TemplateValidator
{
    public static IReadOnlyList<string> Validate(TemplateEntry entry, int promotionRuns = 3)
    {
        var spec = entry.Spec;
        var errors = new List<string>();
        void Check(bool ok, string message)
        {
            if (!ok) errors.Add($"{spec.Id}: {message}");
        }

        Check(KebabId().IsMatch(spec.Id), "id must be kebab-case.");
        Check(SemVer().IsMatch(spec.Version), $"version '{spec.Version}' must be semver (x.y.z).");
        Check(spec.Title.Length > 0, "title is required.");
        Check(spec.Owners.Count > 0, "at least one owner is required.");
        Check(spec.Intent.Keywords.Count > 0, "intent.keywords must not be empty.");
        Check(spec.Outputs.Count > 0, "at least one output is required.");
        Check(spec.Tests.Unit.Count > 0, "templates must ship unit tests (tests.unit).");
        Check(spec.Tests.IntegrationSuggested.Count > 0, "templates must suggest integration tests (tests.integrationSuggested).");

        foreach (var p in spec.Parameters)
        {
            Check(ParamName().IsMatch(p.Name), $"parameter '{p.Name}' must be camelCase.");
            foreach (var (label, rx) in new[] { ("pattern", p.Pattern), ("extract", p.Extract) })
            {
                if (rx is null) continue;
                try { _ = new Regex(rx); }
                catch (ArgumentException ex) { errors.Add($"{spec.Id}: parameter '{p.Name}' {label} is not a valid regex: {ex.Message}"); }
            }
            Check(p.Extract is null || new Regex(p.Extract).GetGroupNumbers().Length > 1, $"parameter '{p.Name}' extract needs a capture group.");
        }

        var sample = spec.Parameters.ToDictionary(p => p.Name, p => SampleValue(p));
        foreach (var o in spec.Outputs.Concat(spec.Tests.Unit).Concat(spec.Tests.IntegrationSuggested))
        {
            Check(File.Exists(Path.Combine(spec.Directory, o.Template)), $"template file '{o.Template}' not found.");
            Check(!o.Path.Contains("..", StringComparison.Ordinal) && !Path.IsPathRooted(o.Path), $"output path '{o.Path}' must be relative and stay inside the repo.");
        }

        if (errors.Count == 0)
        {
            try { _ = TemplateGenerator.Generate(spec, sample); }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or Scriban.Syntax.ScriptRuntimeException)
            {
                errors.Add($"{spec.Id}: rendering with sample parameters failed: {ex.Message}");
            }
        }

        if (spec.State is TemplateState.Approved or TemplateState.Crystallized)
        {
            var prov = entry.Provenance;
            Check(prov.Runs.Count(r => r.Passed) >= promotionRuns, $"state {spec.State} needs ≥{promotionRuns} passing runs in provenance.json.");
            Check(prov.Approvals.Any(a => a.Role == "template-owner"), $"state {spec.State} needs a template-owner approval in provenance.json.");
            Check(prov.Approvals.Any(a => a.Role == "tech-lead"), $"state {spec.State} needs a tech-lead approval in provenance.json.");
        }

        return errors;
    }

    /// <summary>A value that satisfies the parameter's pattern, used to smoke-render the template.</summary>
    static string SampleValue(ParameterSpec p) => p.Name switch
    {
        _ when p.Pattern?.StartsWith("^/", StringComparison.Ordinal) == true => "/samples",
        _ when p.Pattern?.Contains("[A-Z]", StringComparison.Ordinal) == true => "Sample",
        _ => "sample",
    };

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex KebabId();

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex SemVer();

    [GeneratedRegex("^[a-z][A-Za-z0-9]*$")]
    private static partial Regex ParamName();
}
