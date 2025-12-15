using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using WordFrequency;

namespace lab1_SDR
{
    class Program
    {
        static readonly string[] INPUT_DIRS = new[]
        {
            @"C:\fisiere_xml"
        };

        static readonly string STOPWORDS_PATH = "stopwords.txt";

        static readonly string OUTPUT_PATH = Path.Combine(
            @"C:\fisiere_xml",
            $"vectori_frecvente_NEW.txt");

        const int MAX_ATTRIBUTES = 8000;
        static readonly string QUERY_PATH = @"C:\Users\vali1\suntrek\lab1-SDR\lab1-SDR\Interogari de test pentru setul cu 7083 documente.txt";

        const int TOP_RESULTS = 5;

        enum NormalizationMethod
        {
            Nominal,
            Binary,
            Logarithmic,
            SumToOne,
            TfIdf
        }

        static readonly NormalizationMethod NORMALIZATION_METHOD = NormalizationMethod.Nominal;
        enum SimilarityMethod
        {
            Cosine,
            Manhattan,
            Euclidean
        }

        static readonly SimilarityMethod SIMILARITY_METHOD = SimilarityMethod.Cosine;

        readonly record struct AttributeSelection(
            int[] SelectedIndices,
            Dictionary<int, int> Remap,
            Dictionary<int, double> Scores);

        static void Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            var stopwords = LoadStopwords(STOPWORDS_PATH);

            var xmlFiles = INPUT_DIRS
                .Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories)
                    .Where(p => string.Equals(Path.GetExtension(p), ".xml", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (xmlFiles.Count == 0) return;

            var stemmer = new PorterStemmer();
            var processor = new TextProcessor(stemmer, stopwords);

            var docs = new List<Doc>();
            var docTermCounts = new List<Dictionary<int, int>>();
            var termToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var indexToTerm = new List<string>();

            foreach (var path in xmlFiles)
            {
                var doc = ExtractDoc(path);
                var counts = new Dictionary<int, int>();
                foreach (var token in processor.TokenizeNormalizeStem(doc.Content))
                {
                    if (!termToIndex.TryGetValue(token, out var idx))
                    {
                        idx = termToIndex[token] = termToIndex.Count;
                        indexToTerm.Add(token);
                    }

                    counts[idx] = counts.TryGetValue(idx, out var c) ? c + 1 : 1;
                }

                doc.Content = string.Empty;
                docs.Add(doc);
                docTermCounts.Add(counts);
            }

            var documentFrequencies = ComputeDocumentFrequencies(docTermCounts, indexToTerm.Count);
            var selection = SelectBestAttributes(
                docs,
                docTermCounts,
                indexToTerm,
                MAX_ATTRIBUTES);
            var selectedIndices = selection.SelectedIndices;
            var remap = selection.Remap;
            var idfValues = NORMALIZATION_METHOD == NormalizationMethod.TfIdf
                ? ComputeIdfValues(selectedIndices, documentFrequencies, docs.Count)
                : Array.Empty<double>();

            Console.WriteLine(
                $"Atribute selectate: {selectedIndices.Length} / {indexToTerm.Count} (MAX={MAX_ATTRIBUTES})");
            foreach (var idx in selectedIndices)
            {
                double score = TryGetScore(selection.Scores, idx);
                Console.WriteLine($" - {indexToTerm[idx]}: IG={score:F6}");
            }

            if (remap.Count == 0)
            {
                foreach (var doc in docs)
                {
                    doc.Sparse = string.Empty;
                    doc.Vector = new Dictionary<int, double>();
                }
            }
            else
            {
                for (int i = 0; i < docs.Count; i++)
                {
                    var filtered = FilterCounts(docTermCounts[i], remap);
                    var normalized = NormalizeCounts(
                        filtered,
                        NORMALIZATION_METHOD,
                        idfValues);
                    docs[i].Vector = normalized;
                    docs[i].Sparse = BuildSparseRow(normalized);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(OUTPUT_PATH)!);
            using (var w = new StreamWriter(OUTPUT_PATH, false, new UTF8Encoding(false)))
            {
                foreach (var termIndex in selectedIndices)
                {
                    double score = TryGetScore(selection.Scores, termIndex);
                    w.WriteLine($"@attribute {indexToTerm[termIndex]} # IG={score:F6}");
                }
                w.WriteLine("@data");

                foreach (var d in docs)
                {
                    string topics = (d.TopicCodes.Count > 0) ? string.Join(" ", d.TopicCodes) : "";
                    string folder = InferFolderLabel(d.SourcePath);
                    w.WriteLine($"{d.Id} # {d.Sparse} # {topics} # {folder}");
                }
            }

            if (remap.Count > 0)
            {
                RunQueries(
                    QUERY_PATH,
                    processor,
                    docs,
                    termToIndex,
                    remap,
                    NORMALIZATION_METHOD,
                    idfValues);
            }
        }

        static string BuildSparseRow(Dictionary<int, double> counts)
        {
            if (counts.Count == 0) return string.Empty;

            var entries = counts.ToArray();
            Array.Sort(entries, (a, b) => a.Key.CompareTo(b.Key));

            var sb = new StringBuilder(entries.Length * 6);
            bool first = true;
            foreach (var entry in entries)
            {
                double value = entry.Value;
                if (value == 0) continue;
                if (!first) sb.Append(' ');
                first = false;
                sb.Append(entry.Key)
                    .Append(':')
                    .Append(value.ToString("0.######", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        static Dictionary<int, double> NormalizeCounts(
            Dictionary<int, int> counts,
            NormalizationMethod method,
            IReadOnlyList<double> idfValues)
        {
            return method switch
            {
                NormalizationMethod.Binary => NormalizeBinary(counts),
                NormalizationMethod.Logarithmic => NormalizeLogarithmic(counts),
                NormalizationMethod.SumToOne => NormalizeSumToOne(counts),
                NormalizationMethod.TfIdf => NormalizeTfIdf(
                    counts,
                    idfValues),
                _ => NormalizeNominal(counts),
            };
        }

        static Dictionary<int, double> NormalizeNominal(Dictionary<int, int> counts)
        {
            var result = new Dictionary<int, double>(counts.Count);
            foreach (var kvp in counts)
            {
                if (kvp.Value == 0) continue;
                result[kvp.Key] = kvp.Value;
            }
            return result;
        }

        static Dictionary<int, double> NormalizeBinary(Dictionary<int, int> counts)
        {
            var result = new Dictionary<int, double>(counts.Count);
            foreach (var kvp in counts)
            {
                if (kvp.Value > 0)
                    result[kvp.Key] = 1.0;
            }
            return result;
        }

        static Dictionary<int, double> NormalizeLogarithmic(Dictionary<int, int> counts)
        {
            var result = new Dictionary<int, double>(counts.Count);
            foreach (var kvp in counts)
            {
                if (kvp.Value <= 0) continue;
                result[kvp.Key] = 1.0 + Math.Log10(kvp.Value);
            }
            return result;
        }

        static Dictionary<int, double> NormalizeSumToOne(Dictionary<int, int> counts)
        {
            if (counts.Count == 0) return new Dictionary<int, double>();

            double sum = 0.0;
            foreach (var kvp in counts)
                sum += kvp.Value;
            if (sum <= 0) return new Dictionary<int, double>();

            var result = new Dictionary<int, double>(counts.Count);
            foreach (var kvp in counts)
            {
                if (kvp.Value > 0)
                    result[kvp.Key] = kvp.Value / sum;
            }
            return result;
        }

        static Dictionary<int, double> NormalizeTfIdf(
            Dictionary<int, int> counts,
            IReadOnlyList<double> idfValues)
        {
            if (counts.Count == 0 || idfValues.Count == 0)
                return new Dictionary<int, double>();

            var result = new Dictionary<int, double>(counts.Count);
            foreach (var kvp in counts)
            {
                int tf = kvp.Value;
                if (tf <= 0) continue;

                int newIndex = kvp.Key;
                if (newIndex < 0 || newIndex >= idfValues.Count) continue;

                double idf = idfValues[newIndex];
                result[newIndex] = tf * idf;
            }
            return result;
        }

        static Dictionary<int, int> FilterCounts(
            Dictionary<int, int> source,
            Dictionary<int, int> indexMap)
        {
            if (indexMap.Count == 0 || source.Count == 0)
                return new Dictionary<int, int>();

            var filtered = new Dictionary<int, int>(Math.Min(source.Count, indexMap.Count));
            foreach (var kvp in source)
            {
                if (indexMap.TryGetValue(kvp.Key, out var newIdx))
                    filtered[newIdx] = kvp.Value;
            }
            return filtered;
        }

        static int[] ComputeDocumentFrequencies(
            IReadOnlyList<Dictionary<int, int>> docTermCounts,
            int vocabSize)
        {
            var frequencies = new int[vocabSize];
            foreach (var counts in docTermCounts)
            {
                if (counts == null || counts.Count == 0) continue;

                foreach (var termIndex in counts.Keys)
                {
                    if (termIndex >= 0 && termIndex < vocabSize)
                        frequencies[termIndex]++;
                }
            }
            return frequencies;
        }

        static double[] ComputeIdfValues(
            IReadOnlyList<int> selectedIndices,
            IReadOnlyList<int> documentFrequencies,
            int totalDocuments)
        {
            if (selectedIndices.Count == 0 || totalDocuments == 0)
                return Array.Empty<double>();

            var idf = new double[selectedIndices.Count];
            double numerator = totalDocuments + 1.0;
            for (int i = 0; i < selectedIndices.Count; i++)
            {
                int originalIndex = selectedIndices[i];
                int df = (originalIndex >= 0 && originalIndex < documentFrequencies.Count)
                    ? documentFrequencies[originalIndex]
                    : 0;
                idf[i] = Math.Log(numerator / (df + 1)) + 1.0;
            }
            return idf;
        }

        static double TryGetScore(Dictionary<int, double> scores, int termIndex)
            => scores.TryGetValue(termIndex, out var score) ? score : 0.0;

        static AttributeSelection SelectBestAttributes(
            IReadOnlyList<Doc> docs,
            IReadOnlyList<Dictionary<int, int>> docTermCounts,
            IReadOnlyList<string> indexToTerm,
            int maxAttributes)
        {
            int vocabSize = indexToTerm.Count;
            if (docs.Count == 0 || vocabSize == 0 || maxAttributes <= 0)
                return new AttributeSelection(
                    Array.Empty<int>(),
                    new Dictionary<int, int>(),
                    new Dictionary<int, double>());

            var labelToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var labelCounts = new List<int>();
            var docLabelIndices = new int[docs.Count];

            for (int i = 0; i < docs.Count; i++)
            {
                string label = DetermineLabel(docs[i]);
                if (!labelToIndex.TryGetValue(label, out var labelIdx))
                {
                    labelIdx = labelToIndex[label] = labelCounts.Count;
                    labelCounts.Add(0);
                }

                docLabelIndices[i] = labelIdx;
                labelCounts[labelIdx]++;
            }

            int labelCount = labelCounts.Count;
            if (labelCount == 0)
                return new AttributeSelection(
                    Array.Empty<int>(),
                    new Dictionary<int, int>(),
                    new Dictionary<int, double>());

            var labelCountsArray = labelCounts.ToArray();
            var presentDocCounts = new int[vocabSize];
            var presentLabelCounts = new int[vocabSize][];

            for (int docIdx = 0; docIdx < docs.Count; docIdx++)
            {
                var termCounts = docTermCounts[docIdx];
                if (termCounts == null || termCounts.Count == 0) continue;

                int labelIdx = docLabelIndices[docIdx];
                foreach (var kvp in termCounts)
                {
                    int termIdx = kvp.Key;
                    presentDocCounts[termIdx]++;

                    var perLabel = presentLabelCounts[termIdx];
                    if (perLabel == null)
                    {
                        perLabel = new int[labelCount];
                        presentLabelCounts[termIdx] = perLabel;
                    }
                    perLabel[labelIdx]++;
                }
            }

            double baseEntropy = EntropyFromCounts(labelCountsArray, docs.Count);
            var infoGain = new double[vocabSize];

            for (int termIdx = 0; termIdx < vocabSize; termIdx++)
            {
                int presentTotal = presentDocCounts[termIdx];
                if (presentTotal == 0)
                {
                    infoGain[termIdx] = 0.0;
                    continue;
                }

                double condEntropy = 0.0;
                double weightPresent = (double)presentTotal / docs.Count;
                condEntropy += weightPresent * EntropyFromCounts(presentLabelCounts[termIdx], presentTotal);

                int absentTotal = docs.Count - presentTotal;
                if (absentTotal > 0)
                {
                    double weightAbsent = (double)absentTotal / docs.Count;
                    condEntropy += weightAbsent * EntropyForAbsentPartition(
                        labelCountsArray,
                        presentLabelCounts[termIdx],
                        absentTotal);
                }

                infoGain[termIdx] = baseEntropy - condEntropy;
            }

            int take = Math.Min(maxAttributes, vocabSize);
            var ordered = Enumerable.Range(0, vocabSize)
                .OrderByDescending(i => infoGain[i])
                .ThenBy(i => indexToTerm[i], StringComparer.Ordinal)
                .Take(take)
                .ToArray();

            var remap = new Dictionary<int, int>(ordered.Length);
            var scores = new Dictionary<int, double>(ordered.Length);
            for (int i = 0; i < ordered.Length; i++)
            {
                remap[ordered[i]] = i;
                scores[ordered[i]] = infoGain[ordered[i]];
            }

            return new AttributeSelection(ordered, remap, scores);
        }

        static double EntropyFromCounts(int[]? counts, int total)
        {
            if (counts == null || total == 0)
                return 0.0;

            double entropy = 0.0;
            for (int i = 0; i < counts.Length; i++)
            {
                int c = counts[i];
                if (c == 0) continue;
                double p = (double)c / total;
                entropy -= p * Math.Log(p, 2);
            }
            return entropy;
        }

        static double EntropyForAbsentPartition(
            int[] totalLabelCounts,
            int[]? presentCounts,
            int absentTotal)
        {
            if (absentTotal == 0)
                return 0.0;

            double entropy = 0.0;
            for (int i = 0; i < totalLabelCounts.Length; i++)
            {
                int present = presentCounts != null ? presentCounts[i] : 0;
                int absent = totalLabelCounts[i] - present;
                if (absent == 0) continue;

                double p = (double)absent / absentTotal;
                entropy -= p * Math.Log(p, 2);
            }

            return entropy;
        }

        static string DetermineLabel(Doc doc)
        {
            foreach (var code in doc.TopicCodes)
            {
                if (!string.IsNullOrWhiteSpace(code))
                    return code;
            }

            return InferFolderLabel(doc.SourcePath) ?? "Unknown";
        }


        static HashSet<string> LoadStopwords(string path)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (!File.Exists(path)) return set;

            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                var w = line.Trim().ToLowerInvariant();
                if (w.Length > 0 && !w.StartsWith("#"))
                    set.Add(w);
            }
            return set;
        }

        static Doc ExtractDoc(string xmlPath)
        {
            string xml;
            try { xml = File.ReadAllText(xmlPath, Encoding.UTF8); }
            catch { xml = File.ReadAllText(xmlPath, Encoding.GetEncoding("iso-8859-1")); }

            XDocument xdoc;
            try
            {
                xdoc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            }
            catch
            {
                xml = File.ReadAllText(xmlPath, Encoding.GetEncoding("iso-8859-1"));
                xdoc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            }

            var root = xdoc.Root ?? new XElement("doc");

            string id = Path.GetFileNameWithoutExtension(xmlPath);
            string title = root.Element("title")?.Value ?? "";

            string text = "";
            var textNode = root.Element("text");
            if (textNode != null)
            {
                var paragraphs = textNode.Elements("p").Select(p => p.Value ?? "");
                text = string.Join("\n", paragraphs);
            }

            var topicCodes = new List<string>();
            var metadata = root.Element("metadata");
            if (metadata != null)
            {
                foreach (var codes in metadata.Elements("codes"))
                {
                    var cls = (string?)codes.Attribute("class");
                    if (string.Equals(cls, "bip:topics:1.0", StringComparison.Ordinal))
                    {
                        foreach (var code in codes.Elements("code"))
                        {
                            var c = (string?)code.Attribute("code");
                            if (!string.IsNullOrWhiteSpace(c))
                                topicCodes.Add(c!);
                        }
                    }
                }
            }

            return new Doc
            {
                Id = id,
                SourcePath = xmlPath,
                Content = $"{title}\n{text}".Trim(),
                TopicCodes = topicCodes
            };
        }

        static string InferFolderLabel(string path)
        {
            var p = path.ToLowerInvariant();
            if (p.Contains(Path.DirectorySeparatorChar + "testing" + Path.DirectorySeparatorChar) || p.EndsWith($"{Path.DirectorySeparatorChar}testing"))
                return "Testing";
            if (p.Contains(Path.DirectorySeparatorChar + "training" + Path.DirectorySeparatorChar) || p.EndsWith($"{Path.DirectorySeparatorChar}training"))
                return "Training";
            return "Training";
        }

        static void RunQueries(
            string queryPath,
            TextProcessor processor,
            IReadOnlyList<Doc> docs,
            Dictionary<string, int> termToIndex,
            Dictionary<int, int> remap,
            NormalizationMethod normalization,
            IReadOnlyList<double> idfValues)
        {
            if (!File.Exists(queryPath))
            {
                Console.WriteLine($"\nFisierul de interogari nu a fost gasit: {queryPath}");
                return;
            }

            var lines = File.ReadAllLines(queryPath, Encoding.UTF8);
            if (lines.Length == 0)
            {
                Console.WriteLine("\nFisierul de interogari este gol.");
                return;
            }

            Console.WriteLine($"\n=== Rezultate interogari ({SIMILARITY_METHOD}) ===");
            for (int i = 0; i < lines.Length; i++)
            {
                string queryText = lines[i].Trim();
                if (queryText.Length == 0)
                {
                    Console.WriteLine($"\n[Q{i + 1}] Interogare goala.");
                    continue;
                }

                var queryCounts = BuildQueryCounts(queryText, processor, termToIndex, remap);
                var queryVector = NormalizeCounts(queryCounts, normalization, idfValues);
                var ranked = ScoreDocuments(queryVector, docs, SIMILARITY_METHOD);
                PrintQueryResults(i + 1, queryText, ranked);
            }
        }

        static Dictionary<int, int> BuildQueryCounts(
            string queryText,
            TextProcessor processor,
            Dictionary<string, int> termToIndex,
            Dictionary<int, int> remap)
        {
            var counts = new Dictionary<int, int>();
            foreach (var token in processor.TokenizeNormalizeStem(queryText))
            {
                if (!termToIndex.TryGetValue(token, out var originalIndex)) continue;
                if (!remap.TryGetValue(originalIndex, out var mappedIndex)) continue;

                counts[mappedIndex] = counts.TryGetValue(mappedIndex, out var c) ? c + 1 : 1;
            }
            return counts;
        }

        static List<(Doc Doc, double Score)> ScoreDocuments(
            IReadOnlyDictionary<int, double> queryVector,
            IReadOnlyList<Doc> docs,
            SimilarityMethod method)
        {
            var results = new List<(Doc Doc, double Score)>();
            if (queryVector.Count == 0)
                return results;

            foreach (var doc in docs)
            {
                var vector = doc.Vector;
                if (vector == null || vector.Count == 0) continue;

                double score = ComputeSimilarityScore(queryVector, vector, method);
                if (score > 0)
                    results.Add((doc, score));
            }

            results.Sort((a, b) => b.Score.CompareTo(a.Score));
            return results;
        }

        static void PrintQueryResults(
            int queryIndex,
            string queryText,
            List<(Doc Doc, double Score)> ranked)
        {
            Console.WriteLine($"\n[Q{queryIndex}] {queryText}");
            if (ranked.Count == 0)
            {
                Console.WriteLine("  (fara rezultate)");
                return;
            }

            int limit = Math.Min(TOP_RESULTS, ranked.Count);
            for (int i = 0; i < limit; i++)
            {
                var item = ranked[i];
                var doc = item.Doc;
                double score = item.Score;
                string topics = doc.TopicCodes.Count > 0 ? string.Join(" ", doc.TopicCodes) : "N/A";
                Console.WriteLine($"  #{i + 1}: {doc.Id}  score={score:F6}  topics={topics}");
            }
        }

        static double ComputeSimilarityScore(
            IReadOnlyDictionary<int, double> queryVector,
            IReadOnlyDictionary<int, double> docVector,
            SimilarityMethod method)
        {
            return method switch
            {
                SimilarityMethod.Cosine => CosineSimilarity(queryVector, docVector),
                SimilarityMethod.Manhattan => DistanceToSimilarity(ManhattanDistance(queryVector, docVector)),
                SimilarityMethod.Euclidean => DistanceToSimilarity(EuclideanDistance(queryVector, docVector)),
                _ => 0.0
            };
        }

        static double DistanceToSimilarity(double distance)
        {
            if (double.IsNaN(distance) || double.IsInfinity(distance))
                return 0.0;
            return 1.0 / (1.0 + Math.Max(distance, 0.0));
        }

        static double CosineSimilarity(
            IReadOnlyDictionary<int, double> vectorA,
            IReadOnlyDictionary<int, double> vectorB)
        {
            if (vectorA.Count == 0 || vectorB.Count == 0) return 0.0;

            double dot = DotProduct(vectorA, vectorB);
            double normA = Math.Sqrt(SumSquares(vectorA));
            double normB = Math.Sqrt(SumSquares(vectorB));

            if (normA <= 0 || normB <= 0) return 0.0;
            return dot / (normA * normB);
        }
        static double ManhattanDistance(
            IReadOnlyDictionary<int, double> vectorA,
            IReadOnlyDictionary<int, double> vectorB)
        {
            if (vectorA.Count == 0 && vectorB.Count == 0) return 0.0;

            var small = vectorA.Count <= vectorB.Count ? vectorA : vectorB;
            var large = ReferenceEquals(small, vectorA) ? vectorB : vectorA;

            double sum = 0.0;
            foreach (var kvp in small)
            {
                double other = large.TryGetValue(kvp.Key, out var value) ? value : 0.0;
                sum += Math.Abs(kvp.Value - other);
            }
            foreach (var kvp in large)
            {
                if (small.ContainsKey(kvp.Key)) continue;
                sum += Math.Abs(kvp.Value);
            }
            return sum;
        }

        static double EuclideanDistance(
            IReadOnlyDictionary<int, double> vectorA,
            IReadOnlyDictionary<int, double> vectorB)
        {
            if (vectorA.Count == 0 && vectorB.Count == 0) return 0.0;

            var small = vectorA.Count <= vectorB.Count ? vectorA : vectorB;
            var large = ReferenceEquals(small, vectorA) ? vectorB : vectorA;

            double sumSq = 0.0;
            foreach (var kvp in small)
            {
                double other = large.TryGetValue(kvp.Key, out var value) ? value : 0.0;
                double diff = kvp.Value - other;
                sumSq += diff * diff;
            }
            foreach (var kvp in large)
            {
                if (small.ContainsKey(kvp.Key)) continue;
                sumSq += kvp.Value * kvp.Value;
            }
            return Math.Sqrt(sumSq);
        }

        static double DotProduct(
            IReadOnlyDictionary<int, double> vectorA,
            IReadOnlyDictionary<int, double> vectorB)
        {
            if (vectorA.Count == 0 || vectorB.Count == 0) return 0.0;

            var small = vectorA.Count <= vectorB.Count ? vectorA : vectorB;
            var large = ReferenceEquals(small, vectorA) ? vectorB : vectorA;

            double sum = 0.0;
            foreach (var kvp in small)
            {
                if (large.TryGetValue(kvp.Key, out var value))
                    sum += kvp.Value * value;
            }
            return sum;
        }

        static double SumSquares(IReadOnlyDictionary<int, double> vector)
        {
            double sum = 0.0;
            foreach (var value in vector.Values)
                sum += value * value;
            return sum;
        }
    }
}
