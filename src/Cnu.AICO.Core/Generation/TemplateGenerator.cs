using System.Text.Json;
using Cnu.AICO.Core.Matching;
using Cnu.AICO.Core.Specs;
using Scriban;
using Scriban.Runtime;

namespace Cnu.AICO.Core.Generation;

public enum GeneratedKind
{
    Code,
    UnitTest,
    IntegrationSuggested,
}

public sealed record GeneratedFile(string Path, string Content, GeneratedKind Kind, string Source);

public sealed record GenerationResult(
    string TemplateId,
    string Version,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyList<GeneratedFile> Files,
    IReadOnlyList<string> PostActions);

/// <summary>
/// Deterministic code generation from a template spec: same template version + same parameters ⇒ byte-identical output.
/// Every generated code/unit-test file carries a provenance marker so CI can re-generate and diff it
/// (and skip AI review for template-only changes).
/// </summary>
public static class TemplateGenerator
{
    public static GenerationResult Generate(TemplateSpec spec, IReadOnlyDictionary<string, string> parameters)
    {
        var resolved = ParameterResolver.Resolve(spec, parameters);
        if (!resolved.IsComplete)
            throw new ArgumentException(string.Join(" ",
                resolved.Missing.Select(m => $"Missing required parameter '{m}'.").Concat(resolved.Errors)));

        var files = new List<GeneratedFile>();
        files.AddRange(spec.Outputs.Select(o => Render(spec, o, resolved.Values, GeneratedKind.Code)));
        files.AddRange(spec.Tests.Unit.Select(o => Render(spec, o, resolved.Values, GeneratedKind.UnitTest)));
        files.AddRange(spec.Tests.IntegrationSuggested.Select(o => Render(spec, o, resolved.Values, GeneratedKind.IntegrationSuggested)));

        var postActions = spec.PostActions.Select(a => RenderString(a, resolved.Values)).ToList();
        return new GenerationResult(spec.Id, spec.Version, resolved.Values, files, postActions);
    }

    /// <summary>Renders a single output again; used by the regen verifier.</summary>
    public static GeneratedFile? RenderSource(TemplateSpec spec, string source, IReadOnlyDictionary<string, string> parameters)
    {
        var resolved = ParameterResolver.Resolve(spec, parameters);
        foreach (var (outputs, kind) in new[]
                 {
                     (spec.Outputs, GeneratedKind.Code),
                     (spec.Tests.Unit, GeneratedKind.UnitTest),
                     (spec.Tests.IntegrationSuggested, GeneratedKind.IntegrationSuggested),
                 })
        {
            var output = outputs.FirstOrDefault(o => o.Template == source);
            if (output is not null)
                return Render(spec, output, resolved.Values, kind);
        }
        return null;
    }

    /// <summary>Writes the result below <paramref name="root"/>. Refuses to overwrite hand-written files.</summary>
    public static IReadOnlyList<string> WriteTo(string root, GenerationResult result, bool overwrite = false)
    {
        var written = new List<string>();
        foreach (var file in result.Files)
        {
            var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, file.Path));
            if (!target.StartsWith(System.IO.Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Output path '{file.Path}' escapes the target root.");
            if (File.Exists(target) && !overwrite && GeneratedMarker.TryRead(File.ReadAllText(target)) is null)
                throw new InvalidOperationException($"'{file.Path}' exists and is not template-generated; refusing to overwrite.");

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.WriteAllText(target, file.Content);
            written.Add(file.Path);
        }
        return written;
    }

    public static string RenderString(string text, IReadOnlyDictionary<string, string> values)
    {
        var template = Template.Parse(text);
        if (template.HasErrors)
            throw new InvalidDataException(string.Join("; ", template.Messages.Select(m => m.ToString())));

        var globals = new ScriptObject();
        foreach (var (k, v) in values)
            globals.Add(k, v);
        globals.Import("camel", new Func<string, string>(s => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..]));
        globals.Import("plural", new Func<string, string>(Pluralize));

        var context = new TemplateContext { StrictVariables = true, NewLine = "\n" };
        context.PushGlobal(globals);
        return template.Render(context);
    }

    static GeneratedFile Render(TemplateSpec spec, OutputSpec output, IReadOnlyDictionary<string, string> values, GeneratedKind kind)
    {
        var templatePath = System.IO.Path.Combine(spec.Directory, output.Template);
        var body = RenderString(File.ReadAllText(templatePath), values).Replace("\r\n", "\n");
        var path = RenderString(output.Path, values).Replace('\\', '/');
        var marker = kind == GeneratedKind.IntegrationSuggested
            ? GeneratedMarker.Suggested(path, spec)
            : GeneratedMarker.Write(path, new GeneratedMarker.Info(spec.Id, spec.Version, output.Template, values));
        return new GeneratedFile(path, marker + "\n" + body, kind, output.Template);
    }

    static string Pluralize(string s) =>
        s.EndsWith('y') && s.Length > 1 && !"aeiou".Contains(s[^2]) ? s[..^1] + "ies"
        : s.EndsWith('s') || s.EndsWith('x') || s.EndsWith("sh", StringComparison.Ordinal) || s.EndsWith("ch", StringComparison.Ordinal) ? s + "es"
        : s + "s";
}

/// <summary>
/// First-line marker on generated files:
/// <c>// aico:generated template=id@version source=content/x.scriban params=&lt;base64url json&gt;</c>.
/// </summary>
public static class GeneratedMarker
{
    const string Tag = "aico:generated";
    const string SuggestedTag = "aico:suggested";

    public sealed record Info(string TemplateId, string Version, string Source, IReadOnlyDictionary<string, string> Parameters);

    public static string Write(string path, Info info)
    {
        var json = JsonSerializer.Serialize(info.Parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary());
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Comment(path, $"{Tag} template={info.TemplateId}@{info.Version} source={info.Source} params={encoded}");
    }

    public static string Suggested(string path, TemplateSpec spec) =>
        Comment(path, $"{SuggestedTag} template={spec.Id}@{spec.Version} — suggested integration test; adapt it, then delete this line.");

    public static Info? TryRead(string content)
    {
        var firstLine = content.Split('\n', 2)[0];
        var at = firstLine.IndexOf(Tag + " ", StringComparison.Ordinal);
        if (at < 0)
            return null;

        var fields = firstLine[(at + Tag.Length)..].Trim().TrimEnd('-', '>', '*', '/', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Split('=', 2)).Where(f => f.Length == 2).ToDictionary(f => f[0], f => f[1]);
        if (!fields.TryGetValue("template", out var tv) || !fields.TryGetValue("source", out var source) || !fields.TryGetValue("params", out var p))
            return null;

        var idVersion = tv.Split('@', 2);
        var b64 = p.Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(Convert.FromBase64String(b64)) ?? [];
        return new Info(idVersion[0], idVersion.Length > 1 ? idVersion[1] : "", source, parameters);
    }

    static string Comment(string path, string text) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".md" or ".xml" or ".csproj" or ".props" or ".html" or ".razor" => $"<!-- {text} -->",
        ".yaml" or ".yml" or ".sh" or ".ps1" or ".toml" or ".py" => $"# {text}",
        _ => $"// {text}",
    };
}
