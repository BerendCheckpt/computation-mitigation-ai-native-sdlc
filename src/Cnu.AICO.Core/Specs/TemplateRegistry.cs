using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cnu.AICO.Core.Specs;

/// <summary>A template plus its provenance, with the state the router should actually honour.</summary>
public sealed record TemplateEntry(TemplateSpec Spec, Provenance Provenance)
{
    /// <summary>A demoted template is treated as Candidate until a reviewed PR restores it.</summary>
    public TemplateState EffectiveState =>
        Provenance.Demoted && Spec.State == TemplateState.Crystallized ? TemplateState.Candidate : Spec.State;

    public bool IsCrystallized => EffectiveState == TemplateState.Crystallized;
}

/// <summary>Loads every <c>templates/&lt;id&gt;/spec.yaml</c> (and its <c>provenance.json</c>) under a root folder.</summary>
public sealed class TemplateRegistry
{
    public const string SpecFileName = "spec.yaml";
    public const string ProvenanceFileName = "provenance.json";
    public const string ContextFileName = "context.md";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithEnumNamingConvention(LowerCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    readonly Dictionary<string, TemplateEntry> _entries;

    TemplateRegistry(string root, Dictionary<string, TemplateEntry> entries)
    {
        Root = root;
        _entries = entries;
    }

    public string Root { get; }
    public IReadOnlyCollection<TemplateEntry> All => _entries.Values;
    public IEnumerable<TemplateEntry> Crystallized => _entries.Values.Where(e => e.IsCrystallized);

    public TemplateEntry? Find(string id) => _entries.GetValueOrDefault(id);

    public TemplateEntry? FindByToolName(string toolName) =>
        _entries.Values.FirstOrDefault(e => e.Spec.ToolName == toolName);

    public static TemplateRegistry Load(string root)
    {
        root = Path.GetFullPath(root);
        var entries = new Dictionary<string, TemplateEntry>(StringComparer.Ordinal);
        if (!System.IO.Directory.Exists(root))
            return new TemplateRegistry(root, entries);

        foreach (var dir in System.IO.Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            var specPath = Path.Combine(dir, SpecFileName);
            if (!File.Exists(specPath))
                continue;

            var spec = ParseSpec(File.ReadAllText(specPath));
            spec.Directory = dir;
            if (string.IsNullOrWhiteSpace(spec.Id))
                throw new InvalidDataException($"{specPath}: 'id' is required.");
            if (spec.Id != Path.GetFileName(dir))
                throw new InvalidDataException($"{specPath}: id '{spec.Id}' must equal its folder name.");

            entries[spec.Id] = new TemplateEntry(spec, LoadProvenance(dir, spec.Id));
        }

        return new TemplateRegistry(root, entries);
    }

    public static TemplateSpec ParseSpec(string yaml) => Yaml.Deserialize<TemplateSpec>(yaml) ?? new TemplateSpec();

    public static string ReadContext(TemplateSpec spec)
    {
        var path = Path.Combine(spec.Directory, ContextFileName);
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    public void SaveProvenance(TemplateEntry entry)
    {
        var path = Path.Combine(entry.Spec.Directory, ProvenanceFileName);
        File.WriteAllText(path, JsonSerializer.Serialize(entry.Provenance, Json) + Environment.NewLine);
    }

    static Provenance LoadProvenance(string dir, string id)
    {
        var path = Path.Combine(dir, ProvenanceFileName);
        if (!File.Exists(path))
            return new Provenance { TemplateId = id };
        return JsonSerializer.Deserialize<Provenance>(File.ReadAllText(path), Json) ?? new Provenance { TemplateId = id };
    }
}
