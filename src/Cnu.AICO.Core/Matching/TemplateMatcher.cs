using Cnu.AICO.Core.Specs;

namespace Cnu.AICO.Core.Matching;

public sealed record MatchCandidate(string TemplateId, TemplateState State, double Score, double Coverage, bool Vetoed);

public sealed record MatchResult
{
    /// <summary>"deterministic" when a crystallized template can serve the prompt; otherwise "model".</summary>
    public required string Route { get; init; }
    public string? TemplateId { get; init; }
    public string? ToolName { get; init; }
    public double Confidence { get; init; }
    public double Margin { get; init; }
    public bool ExactCacheHit { get; init; }
    public Dictionary<string, string> Parameters { get; init; } = [];
    public IReadOnlyList<string> MissingParameters { get; init; } = [];
    public required string Reason { get; init; }
    public IReadOnlyList<MatchCandidate> Candidates { get; init; } = [];

    public bool IsDeterministic => Route == Routes.Deterministic;
}

public static class Routes
{
    public const string Deterministic = "deterministic";
    public const string Model = "model";
    public const string Human = "human";
}

public sealed record MatcherOptions
{
    /// <summary>Share of the prompt's meaningful terms that must be known to the template (0..1).</summary>
    public double MinConfidence { get; init; } = 0.6;

    /// <summary>Relative lead of the best template over the runner-up: (best − second) / best.</summary>
    public double MinMargin { get; init; } = 0.2;

    public int MinedTermsPerTemplate { get; init; } = 10;
}

/// <summary>
/// Decides, without a model, whether a prompt is fully served by a crystallized template.
/// Pipeline: exact cache → negatives veto → slot extraction → BM25 rank → coverage/margin/slots decision.
/// Anything uncertain is routed to a model, together with the best candidate as context.
/// </summary>
public sealed class TemplateMatcher
{
    readonly TemplateRegistry _registry;
    readonly MatcherOptions _options;
    readonly TextNormalizer _normalizer;
    readonly Bm25Index _index = new();
    readonly Dictionary<string, (string TemplateId, Dictionary<string, string> Parameters)> _exact = new(StringComparer.Ordinal);

    public TemplateMatcher(TemplateRegistry registry, MatcherOptions? options = null)
    {
        _registry = registry;
        _options = options ?? new MatcherOptions();
        var active = registry.All.Where(e => e.EffectiveState != TemplateState.Deprecated).ToList();
        _normalizer = new TextNormalizer(active.SelectMany(e => e.Spec.Intent.Synonyms));

        var mined = TfIdfMiner.TopTerms(
            active.ToDictionary(e => e.Spec.Id, e => e.Provenance.SuccessfulPrompts.SelectMany(PromptTermsWithoutSlotValues).ToList()),
            _options.MinedTermsPerTemplate);

        foreach (var entry in active)
        {
            var terms = entry.Spec.Intent.Keywords.SelectMany(_normalizer.TermsWithBigrams).Concat(mined[entry.Spec.Id]);
            _index.Add(entry.Spec.Id, terms);

            if (entry.IsCrystallized)
                foreach (var sp in entry.Provenance.SuccessfulPrompts)
                    _exact[_normalizer.CacheKey(sp.Prompt)] = (entry.Spec.Id, sp.Parameters);
        }
    }

    public TextNormalizer Normalizer => _normalizer;

    public MatchResult Match(string prompt)
    {
        if (_exact.TryGetValue(_normalizer.CacheKey(prompt), out var hit) && _registry.Find(hit.TemplateId) is { } cached)
        {
            var resolved = ParameterResolver.Resolve(cached.Spec, hit.Parameters);
            if (resolved.IsComplete)
                return new MatchResult
                {
                    Route = Routes.Deterministic,
                    TemplateId = cached.Spec.Id,
                    ToolName = cached.Spec.ToolName,
                    Confidence = 1,
                    Margin = 1,
                    ExactCacheHit = true,
                    Parameters = resolved.Values,
                    Reason = "Exact match with a previously approved prompt.",
                };
        }

        var promptTerms = _normalizer.Terms(prompt).ToHashSet(StringComparer.Ordinal);
        var perTemplate = new Dictionary<string, (Dictionary<string, string> Extracted, List<string> Query, bool Vetoed)>();

        foreach (var entry in _registry.All.Where(e => e.EffectiveState != TemplateState.Deprecated))
        {
            var extracted = ParameterResolver.Extract(entry.Spec, prompt);
            var slotTerms = extracted.Values.SelectMany(_normalizer.Terms).ToHashSet(StringComparer.Ordinal);
            var query = _normalizer.Terms(prompt).Where(t => !slotTerms.Contains(t)).ToList();
            var vetoed = entry.Spec.Intent.Negatives.SelectMany(_normalizer.Terms).Any(promptTerms.Contains);
            perTemplate[entry.Spec.Id] = (extracted, query, vetoed);
        }

        var candidates = new List<MatchCandidate>();
        foreach (var (id, (_, query, vetoed)) in perTemplate)
        {
            var withBigrams = query.Concat(query.Zip(query.Skip(1), (a, b) => a + "_" + b)).ToList();
            var score = _index.Search(withBigrams).FirstOrDefault(r => r.Id == id).Score;
            var distinct = query.Distinct().ToList();
            var coverage = distinct.Count == 0 ? 0 : (double)distinct.Count(t => _index.Contains(id, t)) / distinct.Count;
            candidates.Add(new MatchCandidate(id, _registry.Find(id)!.EffectiveState, Math.Round(score, 4), Math.Round(coverage, 4), vetoed));
        }

        var ranked = candidates.Where(c => !c.Vetoed && c.Score > 0).OrderByDescending(c => c.Score).ToList();
        if (ranked.Count == 0)
            return ModelRoute(candidates.Any(c => c.Vetoed)
                ? "Prompt contains words that mark it as ideation/design work for every related template."
                : "No template vocabulary matches this prompt.", candidates);

        // Only crystallized templates may win, and they compete only with each other: a candidate copy
        // (e.g. a v2 under evaluation) must not block its approved predecessor. A candidate that clearly
        // outscores every crystallized template does mean the prompt is not served by an approved flow.
        var crystallized = ranked.Where(c => _registry.Find(c.TemplateId)!.IsCrystallized).ToList();
        var best = crystallized.FirstOrDefault() ?? ranked[0];
        var bestEntry = _registry.Find(best.TemplateId)!;
        var runnerUp = crystallized.Skip(1).FirstOrDefault();
        var margin = runnerUp is null ? 1 : (best.Score - runnerUp.Score) / best.Score;
        var betterCandidate = ranked[0] is { } top && top.TemplateId != best.TemplateId
            && top.Score > best.Score * (1 + _options.MinMargin) ? top : null;
        var resolvedBest = ParameterResolver.Resolve(bestEntry.Spec, perTemplate[best.TemplateId].Extracted);
        var threshold = bestEntry.Spec.MatchThreshold ?? _options.MinConfidence;

        string? reason =
            !bestEntry.IsCrystallized ? $"Best template '{best.TemplateId}' is {best.State}, not crystallized."
            : betterCandidate is not null ? $"Better served by '{betterCandidate.TemplateId}', which is {betterCandidate.State}, not crystallized."
            : best.Coverage < threshold ? $"Confidence {best.Coverage:0.00} is below threshold {threshold:0.00}."
            : margin < _options.MinMargin ? $"Ambiguous: '{best.TemplateId}' leads '{runnerUp!.TemplateId}' by only {margin:P0}."
            : resolvedBest.Missing.Count > 0 ? $"Missing required parameters: {string.Join(", ", resolvedBest.Missing)}."
            : resolvedBest.Errors.Count > 0 ? string.Join(" ", resolvedBest.Errors)
            : null;

        return new MatchResult
        {
            Route = reason is null ? Routes.Deterministic : Routes.Model,
            TemplateId = best.TemplateId,
            ToolName = bestEntry.Spec.ToolName,
            Confidence = best.Coverage,
            Margin = Math.Round(margin, 4),
            Parameters = resolvedBest.Values,
            MissingParameters = resolvedBest.Missing,
            Reason = reason ?? "Crystallized template covers the prompt; all required parameters resolved.",
            Candidates = candidates.OrderByDescending(c => c.Score).ToList(),
        };
    }

    MatchResult ModelRoute(string reason, IReadOnlyList<MatchCandidate> candidates) => new()
    {
        Route = Routes.Model,
        Reason = reason,
        Candidates = candidates.OrderByDescending(c => c.Score).ToList(),
    };

    IEnumerable<string> PromptTermsWithoutSlotValues(SuccessfulPrompt sp)
    {
        var slotTerms = sp.Parameters.Values.SelectMany(_normalizer.Terms).ToHashSet(StringComparer.Ordinal);
        var terms = _normalizer.Terms(sp.Prompt).Where(t => !slotTerms.Contains(t)).ToList();
        return terms.Concat(terms.Zip(terms.Skip(1), (a, b) => a + "_" + b));
    }
}
