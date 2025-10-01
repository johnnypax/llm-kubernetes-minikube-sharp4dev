#region Impostazioni locali (spostabili in appsettings.json)

// Namespace ammessi dalle policy locali (per evitare azioni su ambienti critici)
using k8s;
using Microsoft.AspNetCore.Mvc;
using Minimal_RAG.Helpers;
using OllamaSharp;
using System.Text.Json;
using System.Text.Json.Serialization;

HashSet<string> AllowedNamespaces = new(StringComparer.OrdinalIgnoreCase)
{ "dev", "staging", "sharp4dev", "test-ns-giovanni" };

// Soglia minima oltre la quale considero "affidabile" un'evidenza
const double EvidenceMinScore = 0.35;

// Modello embedding per RAG
const string EmbedModel = "nomic-embed-text";
#endregion

#region Inizializzazione Servizio Ollama
var ollama = new OllamaApiClient(new Uri("http://127.0.0.1:11434"));
// Modello embedding per RAG
ollama.SelectedModel = "llama3.1:8b";
#endregion

#region Inizializzazione Servizio Kubernetes
var k8sconfig = KubernetesClientConfiguration.BuildConfigFromConfigFile(
    @"C:\Users\ACADEMY\.kube\config");

IKubernetes k8s = new Kubernetes(k8sconfig);
#endregion

#region RAG in-memory: indicizza ./knowledge con chunking (800, overlap 120)
var rag = new RagIndex(EmbedModel);
#endregion

var builder = WebApplication.CreateBuilder(args);

#region Registrazione dei servizi
builder.Services.AddSingleton(ollama);
builder.Services.AddSingleton(k8s);
builder.Services.AddSingleton(rag);
#endregion

var app = builder.Build();
var logger = app.Logger;

// Costruzione indice all'avvio (demo): in produzione potresti farlo on-demand o su background job, ti lascio un tip per sperimentare, al massimo contattami se sei curioso di sapere come farlo ;D
await rag.BuildFromFolderAsync("./knowledge", 800, 120, logger);

app.UseHttpsRedirection();

#region Endpoint base per healthcheck
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
#endregion



#region /rag/search : retrieval "puro" per ispezionare l'indice e recupero Chunk di riferimento

app.MapPost("/rag/search", async (
    [FromServices] RagIndex rag,
    [FromServices] ILoggerFactory lf,
    [FromBody] RagQuery q) =>
{
    var log = lf.CreateLogger("RAG.Search");

    if (string.IsNullOrWhiteSpace(q.Query))
        return Results.BadRequest(new { error = "Query vuota" });

    var hits = await rag.QueryAsync(q.Query, topK: q.TopK is null ? 5 : Math.Clamp(q.TopK.Value, 1, 10));

    if (!hits.Any())
        return Results.Ok(new { info = "Nessun risultato. L'indice potrebbe essere vuoto.", results = Array.Empty<object>() });

    var maxScore = hits.Max(h => h.Score);
    const double minScore = 0.20;

    log.LogInformation("[RAG] Best score = {score}", maxScore);

    if (maxScore < minScore)
    {
        return Results.Ok(new
        {
            info = $"Best score basso ({maxScore}). Aggiungi runbook più pertinenti o verifica l'indice.",
            results = hits.Select(h => new { h.Id, h.Source, h.Score }).ToArray()
        });
    }

    var result = hits.Select(h => new
    {
        h.Id,
        h.Source,
        score = h.Score,
        preview = h.Text.Length > 260 ? h.Text[..260] + "..." : h.Text
    });

    return Results.Ok(result);
});

#endregion

#region /agent_rag : planner RAG-only con "gating"

app.MapPost("/agent_rag", async (
    [FromServices] IKubernetes k8s,
    [FromServices] RagIndex rag,
    [FromServices] OllamaApiClient ollama,
    [FromBody] RagAgentRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Prompt))
        return Results.BadRequest(new { error = "Prompt mancante" });

    // 1) Retrieval: prendo i chunk più rilevanti dai runbook
    var hits = await rag.QueryAsync(req.Prompt, topK: 6);
    if (hits.Count == 0)
        return Results.Ok(new RagAgentResponse(
            result: null,
            citations: Array.Empty<string>(),
            note: "Nessuna evidenza trovata nei runbook."
        ));

    // Seleziono citazioni “sufficienti”: max(best*0.6, 0.35)
    var best = hits.Max(h => h.Score);
    var citations = hits.Where(h => h.Score >= Math.Max(EvidenceMinScore, best * 0.6))
                        .Select(h => h.Id)
                        .ToArray();

    var evidence = hits
        .Where(h => citations.Contains(h.Id))
        .Select(h => new { h.Id, h.Source, h.Score, h.Text })
        .ToList();

    // 2) System: scegli UN SOLO tool, esclusivamente in base all’evidenza
    var system = """
Sei un agente DevOps RAG-only. Puoi scegliere UN SOLO tool tra:
- list_pods {namespace}
- get_logs {namespace, pod, container?}
- scale_deployment {namespace, name, replicas}
- cluster_context {}
- final_answer {}

Regole:
- Usa SOLO le informazioni contenute nell'array 'evidence'. Se una richiesta non è supportata dai runbook presenti in evidence, scegli 'final_answer' spiegando che manca evidenza.
- Non inventare valori. Se mancano 'namespace' o 'name', richiedi informazioni con 'final_answer'.
- Per 'scale_deployment' serve evidenza esplicita (runbook di scaling) e namespace ammesso.
Rispondi SOLO con JSON: {"action":"...", "namespace":"...", "name":"...", "replicas":N, "pod":"...", "container":"..."}.
""";

    var input = new
    {
        user = req.Prompt,
        evidence = evidence.Select(e => new
        {
            e.Id,
            e.Source,
            e.Score,
            text = e.Text.Length > 1500 ? e.Text[..1500] : e.Text
        })
    };

    var toolJson = await Helpers.AskOllamaJsonAsync(ollama, system, input);

    RagAgentToolCall? call = null;
    try
    {
        call = JsonSerializer.Deserialize<RagAgentToolCall>(toolJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }
    catch
    {
        return Results.BadRequest(new { error = "Output del modello non valido", raw = toolJson });
    }

    if (call is null || string.IsNullOrWhiteSpace(call.action))
        return Results.BadRequest(new { error = "Nessuna azione proposta", raw = toolJson });

    // 3) Gating: eseguo SOLO se l’azione è giustificata dall’evidenza
    try
    {
        switch (call.action.ToLowerInvariant())
        {
            case "cluster_context":
                {
                    var ctx = await Helpers.BuildClusterContextAsync(k8s);
                    return Results.Ok(new RagAgentResponse(
                        result: JsonSerializer.Deserialize<object>(ctx)!,
                        citations: citations,
                        note: "cluster_context eseguito sulla base dei runbook."
                    ));
                }

            case "list_pods":
                {
                    var ns = string.IsNullOrWhiteSpace(call.@namespace) ? "default" : call.@namespace!;
                    if (!AllowedNamespaces.Contains(ns))
                        return Results.BadRequest(new { error = $"Namespace '{ns}' non ammesso", citations });

                    var pods = await k8s.CoreV1.ListNamespacedPodAsync(ns);
                    var list = pods.Items.Select(p => new
                    {
                        ns = p.Metadata?.NamespaceProperty,
                        name = p.Metadata?.Name,
                        phase = p.Status?.Phase
                    }).ToArray();

                    return Results.Ok(new RagAgentResponse(
                        result: list,
                        citations: citations,
                        note: "list_pods eseguito (RAG-only)"
                    ));
                }

            case "get_logs":
                {
                    var ns = string.IsNullOrWhiteSpace(call.@namespace) ? "default" : call.@namespace!;
                    if (string.IsNullOrWhiteSpace(call.pod))
                        return Results.BadRequest(new { error = "Manca 'pod' per get_logs", citations });
                    if (!AllowedNamespaces.Contains(ns))
                        return Results.BadRequest(new { error = $"Namespace '{ns}' non ammesso", citations });

                    string logsText;
                    var logsObj = await k8s.CoreV1.ReadNamespacedPodLogAsync(
                        name: call.pod,
                        namespaceParameter: ns,
                        container: call.container,
                        tailLines: 200
                    );

                    if (logsObj is Stream s)
                    {
                        using var reader = new StreamReader(s);
                        logsText = await reader.ReadToEndAsync();
                    }
                    else
                    {
                        logsText = logsObj?.ToString() ?? string.Empty;
                    }

                    if (logsText.Length > 4000)
                        logsText = logsText[..4000] + "\n...[truncated]";

                    return Results.Ok(new RagAgentResponse(
                        result: new { ns, pod = call.pod, container = call.container, logs = logsText },
                        citations: citations,
                        note: "get_logs eseguito (RAG-only)"
                    ));
                }

            case "scale_deployment":
                {
                    // Richiede: namespace ammesso + evidenza di runbook di scaling
                    var ns = string.IsNullOrWhiteSpace(call.@namespace) ? null : call.@namespace;
                    var name = string.IsNullOrWhiteSpace(call.name) ? null : call.name;
                    var replicas = call.replicas;

                    if (ns is null || name is null || replicas is null)
                        return Results.BadRequest(new { error = "Servono 'namespace', 'name' e 'replicas' per scale_deployment", citations });

                    if (!AllowedNamespaces.Contains(ns!))
                        return Results.BadRequest(new { error = $"Namespace '{ns}' non ammesso dalla policy locale", citations });

                    var hasScalingEvidence = evidence.Any(e =>
                        e.Score >= EvidenceMinScore &&
                        (e.Id.Contains("scaling", StringComparison.OrdinalIgnoreCase) ||
                         e.Source.Contains("scaling", StringComparison.OrdinalIgnoreCase) ||
                         e.Text.Contains("scale_deployment", StringComparison.OrdinalIgnoreCase)));

                    if (!hasScalingEvidence)
                        return Results.BadRequest(new { error = "Manca evidenza di runbook di scaling: azione bloccata (RAG-only).", citations });

                    // opzionale: dal front-matter estraggo allowed_namespaces
                    var fmNamespaces = evidence
                        .SelectMany(e => Helpers.ExtractAllowedNamespaces(e.Text))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    if (fmNamespaces.Count > 0 && !fmNamespaces.Contains(ns!))
                        return Results.BadRequest(new { error = $"Namespace '{ns}' non consentito dal runbook (allowed: {string.Join(",", fmNamespaces)})", citations });

                    // ESECUZIONE
                    var scale = await k8s.AppsV1.ReadNamespacedDeploymentScaleAsync(name!, ns!);
                    var prev = scale.Spec?.Replicas ?? 0;
                    scale.Spec!.Replicas = replicas;
                    var updated = await k8s.AppsV1.ReplaceNamespacedDeploymentScaleAsync(scale, name!, ns!);

                    return Results.Ok(new RagAgentResponse(
                        result: new { @namespace = ns, name, replicas_prev = prev, replicas_now = updated.Spec?.Replicas },
                        citations: citations,
                        note: "scale_deployment eseguito perché supportato da runbook (RAG-only)."
                    ));
                }

            case "final_answer":
            default:
                {
                    var summary = new
                    {
                        message = "In base ai runbook recuperati non è possibile eseguire un’azione operativa. Fornisci namespace e deployment, oppure aggiungi un runbook pertinente.",
                        evidence = evidence.Select(e => new { e.Id, e.Score }).ToArray()
                    };
                    return Results.Ok(new RagAgentResponse(
                        result: summary,
                        citations: citations,
                        note: "final_answer (RAG-only)"
                    ));
                }
        }
    }
    catch (Exception ex)
    {
        return Results.Problem(title: "Operazione fallita", detail: ex.Message, statusCode: 500);
    }
});

#endregion

app.Run();

#region Records

// RAG base
record RagQuery(string Query, int? TopK);
record RagChunk(string Id, string Source, string Text, float[] Embedding);
record RagHit(string Id, string Source, string Text, double Score);

// Planner RAG-only
record RagAgentRequest([property: JsonPropertyName("prompt")] string Prompt);
record RagAgentToolCall(string action, string? @namespace, string? name, int? replicas, string? pod, string? container);
record RagAgentResponse(object? result, string[] citations, string? note);

#endregion