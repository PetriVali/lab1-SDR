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
        private readonly Dictionary<string, string> _stemCache = new(StringComparer.Ordinal);

        private static readonly Regex TokenRe = new Regex(@"\p{L}+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public TextProcessor(PorterStemmer stemmer, HashSet<string> stopwords)
        {
            _stemmer = stemmer;
            _stopwords = stopwords;
        }

        public IEnumerable<string> TokenizeNormalizeStem(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;

            var lower = text.ToLowerInvariant();

            foreach (Match m in TokenRe.Matches(lower))
            {
                var token = m.Value;
                if (token.Length == 0 || _stopwords.Contains(token)) continue;

                if (!_stemCache.TryGetValue(token, out var stem))
                {
                    stem = _stemmer.StemWord(token);
                    _stemCache[token] = stem;
                }

                if (string.IsNullOrWhiteSpace(stem) || _stopwords.Contains(stem)) continue;

                yield return stem;
            }
        }

    }
}
