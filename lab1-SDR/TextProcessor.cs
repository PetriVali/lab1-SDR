using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WordFrequency;

namespace lab1_SDR
{
    class TextProcessor
    {
        private readonly PorterStemmer _stemmer;
        private readonly HashSet<string> _stopwords;

       
        private static readonly Regex TokenRe = new Regex(@"\p{L}+", RegexOptions.Compiled);

        public TextProcessor(PorterStemmer stemmer, HashSet<string> stopwords)
        {
            _stemmer = stemmer;
            _stopwords = stopwords;
        }

       public List<string> TokenizeNormalizeStem(string text)
{
    if (string.IsNullOrEmpty(text)) return new List<string>();

    var lower = text.ToLowerInvariant();
    var tokens = new List<string>();
    var stemCache = new Dictionary<string,string>(256, StringComparer.Ordinal); 

    foreach (Match m in TokenRe.Matches(lower))
    {
        var t = m.Value;
        if (t.Length == 0 || _stopwords.Contains(t)) continue;

        if (!stemCache.TryGetValue(t, out var stem))
        {
            stem = _stemmer.StemWord(t);
            stemCache[t] = stem;
        }
        if (string.IsNullOrWhiteSpace(stem) || _stopwords.Contains(stem)) continue;

        tokens.Add(stem);
    }
    return tokens;
}

    }
}
