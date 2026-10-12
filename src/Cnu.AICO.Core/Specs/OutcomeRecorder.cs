namespace Cnu.AICO.Core.Specs;

public sealed record OutcomeResult(
    string TemplateId,
    bool Passed,
    int PassedRuns,
    int ConsecutiveFailures,
    TemplateState EffectiveState,
    string Recommendation);

/// <summary>
/// Records quality-gate outcomes against a template and applies the crystallization rules:
/// N passing runs ⇒ eligible for promotion (a human still approves the PR);
/// repeated failures of a crystallized template ⇒ automatic demotion (the safe direction).
/// </summary>
public sealed class OutcomeRecorder(TemplateRegistry registry, int promotionRuns = 3, int demotionFailures = 2)
{
    public OutcomeResult Record(
        string templateId,
        string runId,
        IReadOnlyDictionary<string, bool> gates,
        string? prompt = null,
        IReadOnlyDictionary<string, string>? parameters = null,
        string? pullRequest = null)
    {
        var entry = registry.Find(templateId) ?? throw new ArgumentException($"Unknown template '{templateId}'.");
        var prov = entry.Provenance;

        var run = new RunRecord { RunId = runId, At = DateTimeOffset.UtcNow, PullRequest = pullRequest, Gates = gates.ToDictionary() };
        prov.Runs.RemoveAll(r => r.RunId == runId);
        prov.Runs.Add(run);

        if (run.Passed && !string.IsNullOrWhiteSpace(prompt)
            && !prov.SuccessfulPrompts.Any(p => p.Prompt.Equals(prompt, StringComparison.OrdinalIgnoreCase)))
            prov.SuccessfulPrompts.Add(new SuccessfulPrompt { Prompt = prompt, Parameters = parameters?.ToDictionary() ?? [] });

        var consecutiveFailures = prov.Runs.AsEnumerable().Reverse().TakeWhile(r => !r.Passed).Count();
        if (entry.Spec.State == TemplateState.Crystallized && consecutiveFailures >= demotionFailures && !prov.Demoted)
        {
            prov.Demoted = true;
            prov.DemotionReason = $"{consecutiveFailures} consecutive gate failures (last run {runId}).";
        }

        registry.SaveProvenance(entry);

        var passedRuns = prov.Runs.Count(r => r.Passed);
        var recommendation = entry.EffectiveState switch
        {
            _ when prov.Demoted => $"Demoted: {prov.DemotionReason} Served by a model until a reviewed PR fixes the template and clears 'demoted'.",
            TemplateState.Exploratory or TemplateState.Candidate when passedRuns >= promotionRuns =>
                $"Eligible for promotion ({passedRuns} passing runs). Open a PR setting state: approved; template owner + tech lead approve.",
            TemplateState.Approved => "Approved. Merge a PR setting state: crystallized to serve it deterministically.",
            TemplateState.Crystallized => "Crystallized; outcome recorded.",
            _ => $"{passedRuns}/{promotionRuns} passing runs needed for promotion.",
        };

        return new OutcomeResult(templateId, run.Passed, passedRuns, consecutiveFailures, entry.EffectiveState, recommendation);
    }
}
