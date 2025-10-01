using System.Text;
using System.Text.RegularExpressions;

namespace Minimal_RAG.Helpers
{
    class RagIndex
    {
        readonly List<RagChunk> _chunks = new();
        readonly string _embedModel;

        public RagIndex(string embedModel) => _embedModel = embedModel;

        // Costruisce indice da cartella: .md .txt .yaml .yml
        public async Task BuildFromFolderAsync(string folder, int chunkSize, int overlap, ILogger logger)
        {
            if (!Directory.Exists(folder))
            {
                logger.LogWarning("[RAG] Cartella non trovata: {folder}", folder);
                return;
            }

            var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                                 .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                                          || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                                          || f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                                          || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                                 .ToArray();

            logger.LogInformation("[RAG] File trovati in {folder}: {count}", folder, files.Length);

            foreach (var file in files)
            {
                logger.LogInformation("[RAG] Indicizzo file: {file}", file);

                // Forza UTF-8
                var text = await File.ReadAllTextAsync(file, Encoding.UTF8);

                var sections = SplitByMarkdownHeaders(text);
                if (sections.Count == 0) sections = ChunkSliding(text, chunkSize, overlap);

                int i = 0, added = 0;
                foreach (var chunk in sections)
                {
                    var clean = Sanitize(chunk);
                    if (string.IsNullOrWhiteSpace(clean)) continue;

                    var emb = await Embedder.EmbedAsync(_embedModel, clean);
                    _chunks.Add(new RagChunk($"{Path.GetFileName(file)}#{i++}", file, clean, emb));
                    added++;
                }

                logger.LogInformation("[RAG] Chunks dal file {file}: {added}", file, added);
            }

            logger.LogInformation("[RAG] Totale chunks indicizzati: {count}", _chunks.Count);
        }


        public async Task<List<RagHit>> QueryAsync(string query, int topK = 5)
        {
            var q = await Embedder.EmbedAsync(_embedModel, query);
            return _chunks
                .Select(c => new RagHit(c.Id, c.Source, c.Text, Cosine(q, c.Embedding)))
                .OrderByDescending(h => h.Score)
                .Take(Math.Max(1, topK))
                .ToList();
        }

        // --- Helpers ---

        static List<string> SplitByMarkdownHeaders(string text)
        {
            // Split su linee che iniziano con #, ##, ### ...
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var acc = new List<string>();
            var sb = new StringBuilder();

            foreach (var line in lines)
            {
                if (Regex.IsMatch(line, @"^\s*#{1,6}\s+"))
                {
                    if (sb.Length > 0) { acc.Add(sb.ToString().Trim()); sb.Clear(); }
                }
                sb.AppendLine(line);
            }
            if (sb.Length > 0) acc.Add(sb.ToString().Trim());

            // ricompatta sezioni troppo lunghe
            var normalized = new List<string>();
            foreach (var s in acc)
            {
                if (s.Length <= 1200) { normalized.Add(s); }
                else
                {
                    normalized.AddRange(ChunkSliding(s, 800, 120));
                }
            }
            return normalized;
        }

        static List<string> ChunkSliding(string text, int size, int overlap)
        {
            var list = new List<string>();
            if (size <= 0) size = 800;
            if (overlap < 0) overlap = 0;

            int step = Math.Max(1, size - overlap);
            for (int i = 0; i < text.Length; i += step)
            {
                int len = Math.Min(size, text.Length - i);
                list.Add(text.Substring(i, len));
            }
            return list;
        }

        static string Sanitize(string s)
        {
            var cleaned = s.Replace("\0", "").Trim();
            // filtro minimo anti-injection
            cleaned = Regex.Replace(cleaned, "(?i)(ignore previous instructions|disregard all prior rules|system prompt)", "[redacted]");
            return cleaned.Length > 2000 ? cleaned[..2000] : cleaned;
        }

        static double Cosine(float[] a, float[] b)
        {
            if (a.Length != b.Length) return -1;
            double dot = 0, na = 0, nb = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                na += a[i] * a[i];
                nb += b[i] * b[i];
            }
            return dot / (Math.Sqrt(na) * Math.Sqrt(nb) + 1e-9);
        }
    }
}
