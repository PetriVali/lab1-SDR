using System;
using System.Collections.Generic;
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

            var selection = SelectBestAttributes(
                docs,
                docTermCounts,
                indexToTerm,
                MAX_ATTRIBUTES);
            var selectedIndices = selection.SelectedIndices;
            var remap = selection.Remap;

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
                    doc.Sparse = string.Empty;
            }
            else
            {
                for (int i = 0; i < docs.Count; i++)
                {
                    var filtered = FilterCounts(docTermCounts[i], remap);
                    docs[i].Sparse = BuildSparseRow(filtered);
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
        }

        static string BuildSparseRow(Dictionary<int, int> counts)
        {
            if (counts.Count == 0) return string.Empty;

            var entries = counts.ToArray();
            Array.Sort(entries, (a, b) => a.Key.CompareTo(b.Key));

            var sb = new StringBuilder(entries.Length * 6);
            bool first = true;
            foreach (var entry in entries)
            {
                if (!first) sb.Append(' ');
                first = false;
                sb.Append(entry.Key).Append(':').Append(entry.Value);
            }
            return sb.ToString();
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
    }
}
