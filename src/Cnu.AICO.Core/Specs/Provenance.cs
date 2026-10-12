namespace Cnu.AICO.Core.Specs;

/// <summary>
/// Evidence trail of a template (<c>templates/&lt;id&gt;/provenance.json</c>): where it came from, every run
/// and its quality-gate results, and who approved it. Feeds promotion, demotion and keyword mining.
/// </summary>
public sealed class Provenance
{
    public string TemplateId { get; set; } = "";

    /// <summary>Free-text note, e.g. that the data is illustrative.</summary>
    public string? Note { get; set; }

    /// <summary>PRs the pattern was crystallized from.</summary>
    public List<string> SourcePullRequests { get; set; } = [];

    public List<Approval> Approvals { get; set; } = [];
    public List<RunRecord> Runs { get; set; } = [];

    /// <summary>Prompts that led to an approved result; mined for TF-IDF keywords and used as an exact-match cache.</summary>
    public List<SuccessfulPrompt> SuccessfulPrompts { get; set; } = [];

    /// <summary>Set automatically on repeated gate failures; the effective state then drops to Candidate until a PR fixes it.</summary>
    public bool Demoted { get; set; }
    public string? DemotionReason { get; set; }
}

public sealed class Approval
{
    public string By { get; set; } = "";
    public string Role { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string? PullRequest { get; set; }
}

public sealed class RunRecord
{
    public string RunId { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string? PullRequest { get; set; }
    public Dictionary<string, bool> Gates { get; set; } = [];
    public bool Passed => Gates.Count > 0 && Gates.Values.All(v => v);
}

public sealed class SuccessfulPrompt
{
    public string Prompt { get; set; } = "";
    public Dictionary<string, string> Parameters { get; set; } = [];
}
