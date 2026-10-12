namespace Cnu.AICO.Core.Matching;

/// <summary>
/// Mines the words that characterise each template from the prompts that led to approved results:
/// frequent in this template's prompts (TF) and rare in other templates' prompts (IDF).
/// The output is appended to the template's BM25 document, so matching improves as approvals accumulate.
/// </summary>
public static class TfIdfMiner
{
    /// <param name="corpus">template id → normalized terms of all its successful prompts.</param>
    public static Dictionary<string, IReadOnlyList<string>> TopTerms(
        IReadOnlyDictionary<string, List<string>> corpus, int top = 10)
    {
        var n = corpus.Count;
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var terms in corpus.Values)
            foreach (var t in terms.Distinct())
                df[t] = df.GetValueOrDefault(t) + 1;

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (id, terms) in corpus)
        {
            if (terms.Count == 0)
            {
                result[id] = [];
                continue;
            }
            // With a single template there is nothing to contrast against, so plain TF ranks the terms.
            // Otherwise a term used by every template has IDF 0 and is dropped: it cannot tell templates apart.
            result[id] = terms
                .GroupBy(t => t, StringComparer.Ordinal)
                .Select(g => (Term: g.Key, Score: (double)g.Count() / terms.Count * (n == 1 ? 1 : Math.Log((double)n / df[g.Key]))))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Term, StringComparer.Ordinal)
                .Take(top)
                .Select(x => x.Term)
                .ToList();
        }
        return result;
    }
}
