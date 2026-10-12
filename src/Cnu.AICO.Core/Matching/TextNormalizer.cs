using System.Text.RegularExpressions;

namespace Cnu.AICO.Core.Matching;

/// <summary>
/// Turns free text into comparable terms without a model: lowercase → tokenize → drop stop words →
/// light stemming → synonym rewriting. The same normalizer is used for prompts and template keywords,
/// so both sides land in the same vocabulary.
/// </summary>
public sealed partial class TextNormalizer
{
    static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "to", "of", "for", "in", "on", "at", "by", "with", "and", "or", "is", "are", "be",
        "it", "this", "that", "these", "those", "please", "can", "could", "would", "you", "me", "my", "we",
        "our", "us", "i", "want", "need", "like", "some", "new", "just", "also", "so", "do", "make", "into",
        "from", "as", "which", "will", "should", "let", "lets", "let's",
    };

    readonly Dictionary<string, string> _synonyms;

    /// <param name="synonyms">canonical → alternatives; alternatives are rewritten to the canonical term.</param>
    public TextNormalizer(IEnumerable<KeyValuePair<string, List<string>>>? synonyms = null)
    {
        _synonyms = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (canonical, alternatives) in synonyms ?? [])
        {
            var c = Stem(canonical.ToLowerInvariant());
            foreach (var alt in alternatives)
                _synonyms[Stem(alt.ToLowerInvariant())] = c;
        }
    }

    /// <summary>Normalized terms in prompt order (duplicates kept, so term frequency survives).</summary>
    public IReadOnlyList<string> Terms(string text)
    {
        var terms = new List<string>();
        foreach (Match m in TokenRegex().Matches(text.ToLowerInvariant()))
        {
            var token = m.Value.Trim('-', '_');
            if (token.Length < 2 || StopWords.Contains(token))
                continue;
            var stem = Stem(token);
            terms.Add(_synonyms.GetValueOrDefault(stem, stem));
        }
        return terms;
    }

    /// <summary>Terms plus adjacent-word bigrams ("minimal_api"), which capture short phrases cheaply.</summary>
    public IReadOnlyList<string> TermsWithBigrams(string text)
    {
        var terms = Terms(text);
        var result = new List<string>(terms);
        for (var i = 0; i + 1 < terms.Count; i++)
            result.Add(terms[i] + "_" + terms[i + 1]);
        return result;
    }

    /// <summary>Stable key for the exact-prompt cache.</summary>
    public string CacheKey(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(string.Join(' ', Terms(text)));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    /// <summary>Deliberately light suffix stripping: predictable beats clever for a routing vocabulary.</summary>
    public static string Stem(string token)
    {
        if (token.Length > 4 && token.EndsWith("ies", StringComparison.Ordinal))
            return token[..^3] + "y";
        if (token.Length > 5 && token.EndsWith("ing", StringComparison.Ordinal))
            return token[..^3];
        if (token.Length > 3 && token.EndsWith('s')
            && !token.EndsWith("ss", StringComparison.Ordinal)
            && !token.EndsWith("us", StringComparison.Ordinal)
            && !token.EndsWith("is", StringComparison.Ordinal))
            return token[..^1];
        return token;
    }

    [GeneratedRegex(@"[a-z0-9][a-z0-9_\-']*")]
    private static partial Regex TokenRegex();
}
