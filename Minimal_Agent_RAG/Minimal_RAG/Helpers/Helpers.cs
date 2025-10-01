using k8s;
using OllamaSharp;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minimal_RAG.Helpers
{
    public static class Helpers
    {
        public static double ParseCpuToMillicores(string? cpu)
        {
            // Restituisce mCPU (millicores)
            if (string.IsNullOrWhiteSpace(cpu)) return 0;
            cpu = cpu.Trim().ToLowerInvariant();

            // nanocores -> mCPU
            if (cpu.EndsWith("n") && double.TryParse(cpu[..^1], out var n))
                return n / 1_000_000.0; // 1e9 n = 1000 m (1 core)

            // microcores -> mCPU
            if (cpu.EndsWith("u") && double.TryParse(cpu[..^1], out var u))
                return u / 1000.0; // 1000u = 1m

            // millicores
            if (cpu.EndsWith("m") && double.TryParse(cpu[..^1], out var m))
                return m;

            // core interi (es. "0.25", "1", "2")
            if (double.TryParse(cpu, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var cores))
                return cores * 1000.0;

            return 0;
        }

        public static double ParseMemToMi(string? mem)
        {
            // Restituisce Mi (Mebibyte)
            if (string.IsNullOrWhiteSpace(mem)) return 0;
            mem = mem.Trim();

            // normalizza suffisso in maiuscolo
            var upper = mem.ToUpperInvariant();

            bool TryParseNum(string s, out double val) =>
                double.TryParse(s, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out val);

            // Binari
            if (upper.EndsWith("KI") && TryParseNum(upper[..^2], out var ki)) return ki / 1024.0;
            if (upper.EndsWith("MI") && TryParseNum(upper[..^2], out var mi)) return mi;
            if (upper.EndsWith("GI") && TryParseNum(upper[..^2], out var gi)) return gi * 1024.0;
            if (upper.EndsWith("TI") && TryParseNum(upper[..^2], out var ti)) return ti * 1024.0 * 1024.0;

            // Decimali (rari, ma gestiamoli): K/M/G senza "i" -> assumiamo base 1000
            if (upper.EndsWith("K") && TryParseNum(upper[..^1], out var k)) return (k * 1000.0) / (1024.0 * 1024.0);
            if (upper.EndsWith("M") && TryParseNum(upper[..^1], out var m)) return (m * 1_000_000.0) / (1024.0 * 1024.0);
            if (upper.EndsWith("G") && TryParseNum(upper[..^1], out var g)) return (g * 1_000_000_000.0) / (1024.0 * 1024.0);

            // Byte puri
            if (TryParseNum(upper, out var bytes)) return bytes / 1024.0 / 1024.0;

            return 0;
        }

        // Estrae allowed_namespaces dal front-matter YAML del runbook (blocco '--- ... ---')
        // NB: soluzione semplice, robusta quanto basta per front-matter standard.
        public static string[] ExtractAllowedNamespaces(string chunkText)
        {
            var start = chunkText.IndexOf("---", StringComparison.Ordinal);
            if (start != 0) return Array.Empty<string>();

            var end = chunkText.IndexOf("---", 3, StringComparison.Ordinal);
            if (end <= start) return Array.Empty<string>();

            var fm = chunkText.Substring(0, end + 3);

            var m = Regex.Match(fm, @"allowed_namespaces:\s*\[(.*?)\]", RegexOptions.IgnoreCase);
            if (!m.Success) return Array.Empty<string>();

            return m.Groups[1].Value.Split(',')
                .Select(s => s.Replace("\"", "").Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();
        }

        // Restituisce un piccolo "context" del cluster (nodi, totali, pods per ns) per risposte veloci
        public static async Task<string> BuildClusterContextAsync(IKubernetes k8s)
        {
            var nodes = await k8s.CoreV1.ListNodeAsync();
            var pods = await k8s.CoreV1.ListPodForAllNamespacesAsync();
            var deploy = await k8s.AppsV1.ListDeploymentForAllNamespacesAsync();

            var summary = new
            {
                nodes = nodes.Items.Select(n => new { n.Metadata.Name, n.Status.NodeInfo.KubeletVersion }).ToArray(),
                totals = new { pods = pods.Items.Count, deployments = deploy.Items.Count },
                podsByNs = pods.Items
                               .GroupBy(p => p.Metadata?.NamespaceProperty ?? "default")
                               .ToDictionary(g => g.Key, g => g.Count())
            };

            return JsonSerializer.Serialize(summary);
        }


        // Chiede ad Ollama una risposta SOLO JSON (con piccola sanificazione dei fence/backticks)
        public static async Task<string> AskOllamaJsonAsync(OllamaApiClient ollama, string system, object input)
        {
            // Prepariamo un input strutturato per ridurre l’allucinazione di formato
            var userJson = JsonSerializer.Serialize(input, new JsonSerializerOptions { WriteIndented = false });
            var prompt = $"{system}\nUtente:\n{userJson}\nRispondi SOLO con JSON valido:";

            var sb = new StringBuilder();
            await foreach (var msg in ollama.GenerateAsync(prompt))
                if (msg != null) sb.Append(msg.Response);

            var raw = sb.ToString().Trim().Trim('`');

            // Taglia tutto prima della prima '{' e dopo l’ultima '}' per cestinare eventuali righe spurie
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start >= 0 && end > start) raw = raw.Substring(start, end - start + 1);

            // (Opzionale) Verifica minima che sembri JSON
            // Se fallisce, restituiremo il raw così com’è: il chiamante farà try/catch di deserializzazione.
            return raw;
        }

    }
}
