using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cnu.AICO.Core.Telemetry;

public sealed record DecisionEntry
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public required string Tool { get; init; }
    public required string Route { get; init; }
    public string? TemplateId { get; init; }
    public string? Context { get; init; }
    public double? Confidence { get; init; }

    /// <summary>Output tokens avoided by serving this deterministically (see <see cref="BaselineSource"/>).</summary>
    public int TokensAvoided { get; init; }

    /// <summary>"measured" when the template has a measured baseline, "estimated" when it falls back to the configured guess.</summary>
    public string? BaselineSource { get; init; }
    public string? Reason { get; init; }
}

/// <summary>Append-only JSONL log of routing decisions; the input for the KPI report.</summary>
public sealed class DecisionLog(string? path)
{
    /// <summary>
    /// Assumed cost of a model emitting a tool call with parameters. Only used for estimated baselines;
    /// a measured baseline already contains the real cost of the template path.
    /// </summary>
    public const int ToolCallTokens = 50;

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    readonly Lock _gate = new();

    public string? Path { get; } = path;

    public void Append(DecisionEntry entry)
    {
        if (string.IsNullOrEmpty(Path))
            return;
        var line = JsonSerializer.Serialize(entry, Json) + "\n";
        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
            File.AppendAllText(Path, line);
        }
    }

    public static IEnumerable<DecisionEntry> Read(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            DecisionEntry? entry;
            try { entry = JsonSerializer.Deserialize<DecisionEntry>(line, Json); }
            catch (JsonException) { continue; }
            if (entry is not null)
                yield return entry;
        }
    }
}
