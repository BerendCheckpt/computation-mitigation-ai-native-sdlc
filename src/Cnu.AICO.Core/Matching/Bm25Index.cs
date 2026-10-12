namespace Cnu.AICO.Core.Matching;

/// <summary>
/// Okapi BM25 over a small set of documents (one per template). Plain arithmetic, no model:
/// rare terms weigh more (IDF), repeated terms saturate (k1), and long keyword lists are not
/// rewarded for length alone (b). See the architecture docs, appendix A1.
/// </summary>
public sealed class Bm25Index
{
    readonly double _k1;
    readonly double _b;
    readonly List<(string Id, Dictionary<string, int> Tf, int Length)> _docs = [];
    readonly Dictionary<string, int> _df = new(StringComparer.Ordinal);

    public Bm25Index(double k1 = 1.2, double b = 0.75)
    {
        _k1 = k1;
        _b = b;
    }

    public int Count => _docs.Count;

    public void Add(string id, IEnumerable<string> terms)
    {
        var tf = new Dictionary<string, int>(StringComparer.Ordinal);
        var length = 0;
        foreach (var t in terms)
        {
            tf[t] = tf.GetValueOrDefault(t) + 1;
            length++;
        }
        foreach (var t in tf.Keys)
            _df[t] = _df.GetValueOrDefault(t) + 1;
        _docs.Add((id, tf, length));
    }

    public bool Contains(string id, string term) =>
        _docs.FirstOrDefault(d => d.Id == id).Tf?.ContainsKey(term) == true;

    /// <summary>Scores every document; highest first. Documents with score 0 are omitted.</summary>
    public IReadOnlyList<(string Id, double Score)> Search(IReadOnlyCollection<string> query)
    {
        if (_docs.Count == 0)
            return [];

        var avgLength = _docs.Average(d => d.Length);
        var n = _docs.Count;
        var results = new List<(string, double)>();

        foreach (var (id, tf, length) in _docs)
        {
            double score = 0;
            foreach (var q in query.Distinct())
            {
                if (!tf.TryGetValue(q, out var f))
                    continue;
                var df = _df[q];
                var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                score += idf * (f * (_k1 + 1)) / (f + _k1 * (1 - _b + _b * length / avgLength));
            }
            if (score > 0)
                results.Add((id, score));
        }

        return results.OrderByDescending(r => r.Item2).ThenBy(r => r.Item1, StringComparer.Ordinal).ToList();
    }
}
