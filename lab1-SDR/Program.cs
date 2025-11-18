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
            var termToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var indexToTerm = new List<string>();

            foreach (var path in xmlFiles)
            {
                var doc = ExtractDoc(path);
                var tokens = processor.TokenizeNormalizeStem(doc.Content);
                doc.Terms = tokens;

                foreach (var t in tokens)
                {
                    if (!termToIndex.ContainsKey(t))
                    {
                        int idx = termToIndex[t] = termToIndex.Count;
                        indexToTerm.Add(t);
                    }
                }

                docs.Add(doc);
            }

            foreach (var d in docs)
            {
                var counts = new Dictionary<int, int>();
                foreach (var term in d.Terms)
                {
                    int idx = termToIndex[term];
                    counts[idx] = counts.TryGetValue(idx, out var c) ? c + 1 : 1;
                }

                var sb = new StringBuilder();
                foreach (var kv in counts.OrderBy(kv => kv.Key))
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(kv.Key).Append(':').Append(kv.Value);
                }
                d.Sparse = sb.ToString();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(OUTPUT_PATH)!);
            using (var w = new StreamWriter(OUTPUT_PATH, false, new UTF8Encoding(false)))
            {
                foreach (var term in indexToTerm)
                    w.WriteLine($"@attribute {term}");
                w.WriteLine("@data");

                foreach (var d in docs)
                {
                    string topics = (d.TopicCodes.Count > 0) ? string.Join(" ", d.TopicCodes) : "";
                    string folder = InferFolderLabel(d.SourcePath);
                    w.WriteLine($"{d.Id} # {d.Sparse} # {topics} # {folder}");
                }
            }
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
