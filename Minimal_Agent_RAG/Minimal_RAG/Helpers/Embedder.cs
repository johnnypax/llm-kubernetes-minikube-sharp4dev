using System.Text;
using System.Text.Json;

namespace Minimal_RAG.Helpers
{
    //Client minimale per embeddings di Ollama
    static class Embedder
    {
        static readonly HttpClient http = new HttpClient { BaseAddress = new Uri("http://localhost:11434") };

        public static async Task<float[]> EmbedAsync(string model, string text)
        {
            // 1) Tentativo standard Ollama: { model, input: string }
            var payload = JsonSerializer.Serialize(new { model, input = text });
            var emb = await TryEmbed(payload);
            if (emb is not null && emb.Length > 0) return emb;

            // 2) Alcune build accettano array di input
            payload = JsonSerializer.Serialize(new { model, input = new[] { text } });
            emb = await TryEmbed(payload);
            if (emb is not null && emb.Length > 0) return emb;

            // 3) Alcune build leggono "prompt"
            payload = JsonSerializer.Serialize(new { model, prompt = text });
            emb = await TryEmbed(payload);
            if (emb is not null && emb.Length > 0) return emb;

            throw new InvalidOperationException("Impossibile ottenere embeddings da Ollama. Verifica il modello/endpoint.");
        }

        static async Task<float[]?> TryEmbed(string jsonPayload)
        {
            using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync("/api/embeddings", content);
            if (!resp.IsSuccessStatusCode) return null;

            var s = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;

            // Formati possibili:
            // A) { "embedding": [ ... ] }
            if (root.TryGetProperty("embedding", out var single) && single.ValueKind == JsonValueKind.Array)
                return single.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();

            // B) { "embeddings": [ [ ... ], ... ] }
            if (root.TryGetProperty("embeddings", out var multi) && multi.ValueKind == JsonValueKind.Array)
            {
                var first = multi.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Array)
                    return first.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
            }

            // C) stile OpenAI-like: { "data":[{"embedding":[...]}] }
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                var first = data.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("embedding", out var e) && e.ValueKind == JsonValueKind.Array)
                    return e.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
            }

            return null;
        }
    }
}
