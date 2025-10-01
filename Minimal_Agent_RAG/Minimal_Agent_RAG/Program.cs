using k8s;
using Microsoft.AspNetCore.Mvc;
using OllamaSharp;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

#region Inizializzazione Servizio Ollama
var ollama = new OllamaApiClient(new Uri("http://127.0.0.1:11434"));
ollama.SelectedModel = "llama3.1:8b";
#endregion

#region Inizializzazione Servizio Kubernetes
var k8sconfig = KubernetesClientConfiguration.BuildConfigFromConfigFile(
    @"C:\Users\ACADEMY\.kube\config");

IKubernetes k8s = new Kubernetes(k8sconfig);
#endregion

builder.Services.AddSingleton(ollama);
builder.Services.AddSingleton(k8s);

var app = builder.Build();

app.UseHttpsRedirection();

app.MapGet("/health", () =>
{
    return Results.Ok(new { status = "OK"});
});

app.MapPost("/agent", async (   [FromServices] OllamaApiClient ollama,
                                [FromServices] IKubernetes k8s,
                                [FromBody] AskRequest req) =>
{
    #region Chiedi ad Ollama di produrre solo JSON
    string systemPrompt = """
        Sei un assistente DevOps per Kubernetes. Rispondi SOLO in JSON
        Scegli uno tra: list_pods, get_logs e scale_deployment.
        - list_pods:        {"action":"list_pods","namespace": "..."}
        - get_logs:         {"action":"get_logs","namespace":"...","pod":"...","container":"opzionale"}
        - scale_deployment: {"action":"scale_deployment","namespace":"...","name":"...","replicas":"NUM"}
        Nessun testo al di fuori del JSON
    """;

    var fullPrompt = $"{systemPrompt}\nUtente: {req.Prompt}\nRisposta JSON:";

    var sb = new StringBuilder();

    await foreach(var msg in ollama.GenerateAsync(fullPrompt))
    {
        if (msg != null)
            sb.Append(msg.Response);
    }

    var rawString = sb.ToString();      //Restituisco il JSON di tipo {"action...
    #endregion

    #region 2. Deserializzazione dell'oggetto JSON restituito
    CallAction? call;
    try
    {
        call = JsonSerializer.Deserialize<CallAction>(rawString, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        if(call is null || string.IsNullOrWhiteSpace(call.action))
            return Results.BadRequest(
                new { error = "Output del modello non valido", data = rawString });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(
            new { error = "JSON Parse error", cause = ex.Message, data = rawString });
    }
    #endregion

    #region 3. Invio comandi a K8s
    switch (call.action.ToLowerInvariant())
    {
        case "list_pods":
            {
                var ns = string.IsNullOrWhiteSpace(call.@namespace) ? "default" : call.@namespace;
                var pods = await k8s.CoreV1.ListNamespacedPodAsync(ns);
                var list = pods.Items.Select(p => new
                {
                    ns = p.Metadata?.NamespaceProperty,
                    name = p.Metadata?.Name,
                    phase = p.Status?.Phase,
                    node = p.Spec?.NodeName
                });

                return Results.Ok(
                    new { action = call.action, ns = call.@namespace, pods = list });
            }
            

        case "get_logs":
            {
                var ns = string.IsNullOrWhiteSpace(call.@namespace) ? "default" : call.@namespace;
                if (string.IsNullOrWhiteSpace(call.pod))
                    return Results.BadRequest(
                        new { error = "Missing pod name" });

                await using var stream = await k8s.CoreV1.ReadNamespacedPodLogAsync(
                    name: call.pod,
                    namespaceParameter: ns,
                    container: string.IsNullOrWhiteSpace(call.container) ? null : call.container
                   );

                using var reader = new StreamReader(stream, Encoding.UTF8, true);
                var logs = await reader.ReadToEndAsync();

                return Results.Ok(new
                {
                    action = call.action,
                    ns = ns,   
                    pod = call.pod,
                    logs
                });

            }

        case "scale_deployment":
            {
                var ns = string.IsNullOrWhiteSpace(call.@namespace) ? "default" : call.@namespace;
                if(string.IsNullOrWhiteSpace(call.name) || call.replicas is null)
                    return Results.BadRequest(
                        new { error = "Missing name or replicas for scale_deployment" });

                var scale = await k8s.AppsV1.ReadNamespacedDeploymentScaleAsync(call.name, ns);
                scale.Spec.Replicas = call.replicas;
                var updated = await k8s.AppsV1.ReplaceNamespacedDeploymentScaleAsync(scale, call.name, ns);

                return Results.Ok(new
                {
                    action = call.action,
                    ns = ns,
                    deployment = call.name,
                    replicas = updated.Spec?.Replicas
                });
            }

        default:
            return Results.BadRequest(
                new { error = "Azione non supportata"  });
    }
    #endregion
});

app.Run();

#region Record
record AskRequest([property: JsonPropertyName("prompt")]string Prompt);

record CallAction(string action, string @namespace, string? pod, string? container, string? name, int? replicas);

#endregion