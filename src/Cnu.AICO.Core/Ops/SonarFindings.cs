using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Cnu.AICO.Core.Ops;

public enum SonarCategory { Bloat, Convention, Bug }

/// <summary>One impact of a SonarQube issue (Clean Code taxonomy): quality MAINTAINABILITY/RELIABILITY/SECURITY, severity INFO…BLOCKER.</summary>
public sealed record SonarImpact(string SoftwareQuality, string Severity);

/// <summary>One issue from <c>api/issues/search</c>. <see cref="Severity"/> is the legacy severity (INFO…BLOCKER).</summary>
public sealed record SonarIssue(
    string Key,
    string Rule,
    string File,
    int? Line,
    string Message,
    string? Severity,
    IReadOnlyList<string> Tags,
    IReadOnlyList<SonarImpact> Impacts,
    string? Type = null);

public sealed record SonarPage(IReadOnlyList<SonarIssue> Issues, int Total);

/// <summary>
/// Groups SonarQube findings into bloat, conventions and bugs, for the local <c>aico-ci sonar</c> command.
/// Scope is maintainability and reliability only; security issues and hotspots are never requested or reported.
/// </summary>
public static class SonarFindings
{
    public const int PageSize = 500;

    /// <summary>
    /// Bloat heuristic: a maintainability issue is bloat when one of its tags, or its rule, is about size, complexity,
    /// duplication or code that does nothing. Everything else that is maintainability is a convention finding.
    /// </summary>
    static readonly HashSet<string> BloatTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "brain-overload", "size", "complexity", "duplicate", "duplication", "unused", "redundant", "dead-code",
    };

    // Rules without one of the tags above: duplicated blocks; too many parameters/lines/statements; complexity.
    static readonly HashSet<string> BloatRules = new(StringComparer.OrdinalIgnoreCase)
    {
        "DuplicatedBlocks", "S104", "S107", "S138", "S1067", "S1541", "S3776", "S1144", "S1172", "S1481", "S1854",
    };

    static readonly string[] BlockingImpacts = ["BLOCKER", "HIGH"];
    static readonly string[] BlockingLegacy = ["BLOCKER", "CRITICAL"];
    static readonly string[] Order = ["BLOCKER", "HIGH", "CRITICAL", "MEDIUM", "MAJOR", "LOW", "MINOR", "INFO"];

    public static string SearchPath(string project, int page) =>
        $"api/issues/search?componentKeys={Uri.EscapeDataString(project)}&impactSoftwareQualities=MAINTAINABILITY,RELIABILITY" +
        $"&issueStatuses=OPEN,CONFIRMED&ps={PageSize}&p={page.ToString(CultureInfo.InvariantCulture)}";

    public static SonarPage ParsePage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var total = root.TryGetProperty("paging", out var paging) && paging.TryGetProperty("total", out var t) && t.TryGetInt32(out var n)
            ? n
            : root.TryGetProperty("total", out var legacy) && legacy.TryGetInt32(out var m) ? m : 0;
        var issues = new List<SonarIssue>();
        if (root.TryGetProperty("issues", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in arr.EnumerateArray())
            {
                var component = Str(i, "component") ?? "";
                var colon = component.IndexOf(':', StringComparison.Ordinal);
                issues.Add(new SonarIssue(
                    Key: Str(i, "key") ?? "",
                    Rule: Str(i, "rule") ?? "",
                    File: colon >= 0 ? component[(colon + 1)..] : component,
                    Line: i.TryGetProperty("line", out var l) && l.TryGetInt32(out var line) ? line : null,
                    Message: Str(i, "message") ?? "",
                    Severity: Str(i, "severity"),
                    Tags: i.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                        ? tags.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                        : [],
                    Impacts: i.TryGetProperty("impacts", out var imp) && imp.ValueKind == JsonValueKind.Array
                        ? imp.EnumerateArray().Select(x => new SonarImpact(Str(x, "softwareQuality") ?? "", Str(x, "severity") ?? "")).ToList()
                        : [],
                    Type: Str(i, "type")));
            }
        }
        return new SonarPage(issues, total);
    }

    /// <summary>Bug = a reliability impact (or legacy type BUG); bloat = see <see cref="BloatTags"/>; else convention.</summary>
    public static SonarCategory Classify(SonarIssue issue)
    {
        if (issue.Impacts.Any(i => i.SoftwareQuality == "RELIABILITY") || issue.Type == "BUG")
            return SonarCategory.Bug;
        var rule = issue.Rule[(issue.Rule.IndexOf(':', StringComparison.Ordinal) + 1)..];
        return issue.Tags.Any(BloatTags.Contains) || BloatRules.Contains(rule) ? SonarCategory.Bloat : SonarCategory.Convention;
    }

    /// <summary>A Blocker/High maintainability or reliability impact, or a legacy Blocker/Critical severity.</summary>
    public static bool IsBlocking(SonarIssue issue) =>
        InScope(issue).Any(i => BlockingImpacts.Contains(i.Severity)) || (issue.Severity is { } s && BlockingLegacy.Contains(s));

    /// <summary>The highest in-scope impact severity, else the legacy severity.</summary>
    public static string SeverityOf(SonarIssue issue) =>
        InScope(issue).Select(i => i.Severity).OrderBy(Rank).FirstOrDefault() ?? issue.Severity ?? "UNKNOWN";

    public static string Report(string project, IEnumerable<SonarIssue> issues)
    {
        var list = issues.OrderBy(i => i.File, StringComparer.Ordinal).ThenBy(i => i.Line ?? 0)
            .ThenBy(i => i.Rule, StringComparer.Ordinal).ThenBy(i => i.Key, StringComparer.Ordinal).ToList();
        var groups = list.GroupBy(Classify).ToDictionary(g => g.Key, g => g.ToList());
        int Count(SonarCategory c) => groups.TryGetValue(c, out var g) ? g.Count : 0;

        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(ci, $"## SonarQube: {project}\n\n");
        sb.Append(ci, $"{list.Count} finding(s): {Count(SonarCategory.Bloat)} bloat, {Count(SonarCategory.Convention)} conventions, {Count(SonarCategory.Bug)} bugs. ");
        sb.Append(ci, $"Blocking (Blocker/High): {list.Count(IsBlocking)}. Maintainability and reliability only; no security.\n");
        foreach (var (category, title) in new[] { (SonarCategory.Bloat, "Bloat"), (SonarCategory.Convention, "Conventions"), (SonarCategory.Bug, "Bugs") })
        {
            sb.Append(ci, $"\n### {title}\n\n");
            if (!groups.TryGetValue(category, out var items))
            {
                sb.Append("None.\n");
                continue;
            }
            foreach (var i in items)
            {
                var where = i.Line is { } line ? $"{i.File}:{line.ToString(ci)}" : i.File;
                sb.Append(ci, $"- `{where}` {SeverityOf(i)} `{i.Rule}` {i.Message.ReplaceLineEndings(" ")}\n");
            }
        }
        return sb.ToString();
    }

    static IEnumerable<SonarImpact> InScope(SonarIssue issue) =>
        issue.Impacts.Where(i => i.SoftwareQuality is "MAINTAINABILITY" or "RELIABILITY");

    static int Rank(string severity) => Array.IndexOf(Order, severity) is var i and >= 0 ? i : Order.Length;

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
