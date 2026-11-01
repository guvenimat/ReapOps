using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ReapOps.Application.Diagnosis;
using ReapOps.Domain.Agent;

namespace ReapOps.Infrastructure.Agent;

public sealed class OpenAiCompatibleModel : IDiagnosisModel, IDisposable
{
    public const string SubmitTool = "submit_diagnosis";
    const int MaxAttempts = 4;

    readonly HttpClient _http;
    readonly string _model;
    readonly ILogger<OpenAiCompatibleModel> _log;

    public OpenAiCompatibleModel(IConfiguration cfg, ILogger<OpenAiCompatibleModel> log)
    {
        _log = log;
        _model = cfg["Llm:Model"] ?? "gemini-3.5-flash";
        var baseUrl = cfg["Llm:BaseUrl"]!.TrimEnd('/') + "/";
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(int.TryParse(cfg["Llm:TimeoutSeconds"], out var s) ? s : 120) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg["Llm:ApiKey"]);
        Name = $"{_model} ({_http.BaseAddress.Host})";
    }

    public static bool IsEnabled(IConfiguration cfg) => !string.IsNullOrWhiteSpace(cfg["Llm:ApiKey"]) && !string.IsNullOrWhiteSpace(cfg["Llm:BaseUrl"]);

    public string Name { get; }
    public bool IsConfigured => true;

    public async Task<ModelReply> NextAsync(ModelRequest request, CancellationToken ct)
    {
        var body = BuildRequest(_model, request).ToJsonString();
        for (var attempt = 1; ; attempt++)
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var res = await _http.PostAsync("chat/completions", content, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode) return ParseReply(text);
            if (attempt < MaxAttempts && res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout)
            {
                var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(4, attempt));
                _log.LogInformation("Model returned {Status}; retrying in {Seconds} s", (int)res.StatusCode, wait.TotalSeconds);
                await Task.Delay(wait, ct);
                continue;
            }
            throw new InvalidOperationException($"Model API returned {(int)res.StatusCode}: {(text.Length > 400 ? text[..400] : text)}");
        }
    }

    public static JsonObject BuildRequest(string model, ModelRequest request)
    {
        var messages = new JsonArray((JsonNode)new JsonObject { ["role"] = "system", ["content"] = request.System });
        foreach (var turn in request.Transcript)
        {
            if (turn.Text is { } text) messages.Add((JsonNode)new JsonObject { ["role"] = turn.Role, ["content"] = text });
            if (turn.Calls.Count > 0)
            {
                var calls = new JsonArray();
                foreach (var c in turn.Calls)
                {
                    var call = new JsonObject
                    {
                        ["id"] = c.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = Arguments(c.Arguments) },
                    };
                    if (c.Opaque is { } opaque) call["extra_content"] = JsonNode.Parse(opaque);
                    calls.Add((JsonNode)call);
                }
                messages.Add((JsonNode)new JsonObject { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = calls });
            }
            foreach (var r in turn.Results)
                messages.Add((JsonNode)new JsonObject { ["role"] = "tool", ["tool_call_id"] = r.CallId, ["content"] = r.IsError ? "ERROR: " + r.Content : r.Content });
        }

        var tools = new JsonArray();
        foreach (var t in request.Tools) tools.Add((JsonNode)Function(t.Name, t.Description, ToolSchema(t)));
        tools.Add((JsonNode)Function(SubmitTool, "Submit your final diagnosis. Call it exactly once, when you are done investigating.", DiagnosisSchema(request.Actions)));
        return new JsonObject { ["model"] = model, ["messages"] = messages, ["tools"] = tools, ["tool_choice"] = "auto" };
    }

    static string Arguments(IReadOnlyDictionary<string, string> args)
    {
        var o = new JsonObject();
        foreach (var (k, v) in args) o[k] = v;
        return o.ToJsonString();
    }

    static JsonObject Function(string name, string description, JsonObject parameters) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject { ["name"] = name, ["description"] = description, ["parameters"] = parameters },
    };

    static JsonObject ToolSchema(ToolSpec spec)
    {
        var props = new JsonObject();
        foreach (var p in spec.Parameters) props[p.Name] = new JsonObject { ["type"] = "string", ["description"] = p.Description };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray(spec.Parameters.Where(p => p.Required).Select(p => (JsonNode)p.Name).ToArray()),
        };
    }

    static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)v).ToArray());

    static JsonObject DiagnosisSchema(IReadOnlyList<CatalogAction> actions)
    {
        var catalog = string.Join("; ", actions.Select(a => $"{a.Id} ({a.Risk.ToString().ToLowerInvariant()} risk; parameters: {(a.Parameters.Count == 0 ? "none" : string.Join(", ", a.Parameters))}): {a.Description}"));
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["summary"] = new JsonObject { ["type"] = "string", ["description"] = "One or two sentences an operator reads first" },
                ["root_cause"] = new JsonObject { ["type"] = "string", ["description"] = "The most likely cause, specific to this system" },
                ["confidence"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(["low", "medium", "high"]) },
                ["evidence"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["statement"] = new JsonObject { ["type"] = "string" },
                            ["sources"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Ids of the tool calls this claim comes from" },
                        },
                        ["required"] = Strings(["statement", "sources"]),
                    },
                },
                ["alternatives"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["hypothesis"] = new JsonObject { ["type"] = "string" }, ["why"] = new JsonObject { ["type"] = "string" } },
                        ["required"] = Strings(["hypothesis", "why"]),
                    },
                },
                ["options"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "At most three, safest useful first. Catalog: " + catalog,
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["action_id"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(actions.Select(a => a.Id)) },
                            ["parameters"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject { ["name"] = new JsonObject { ["type"] = "string" }, ["value"] = new JsonObject { ["type"] = "string" } },
                                    ["required"] = Strings(["name", "value"]),
                                },
                            },
                            ["rationale"] = new JsonObject { ["type"] = "string" },
                        },
                        ["required"] = Strings(["action_id", "rationale"]),
                    },
                },
            },
            ["required"] = Strings(["summary", "root_cause", "confidence", "evidence", "alternatives", "options"]),
        };
    }

    public static ModelReply ParseReply(string json)
    {
        var root = JsonNode.Parse(json)!;
        var choice = root["choices"]?[0];
        var usage = root["usage"];
        var input = usage?["prompt_tokens"]?.GetValue<int>() ?? 0;
        var total = usage?["total_tokens"]?.GetValue<int>() ?? 0;
        var output = Math.Max(usage?["completion_tokens"]?.GetValue<int>() ?? 0, total - input);
        var finish = choice?["finish_reason"]?.GetValue<string>();
        if (finish == "content_filter") return new ModelReply([], null, "content_filter", input, output);

        var calls = new List<ToolCall>();
        DiagnosisDraft? final = null;
        if (choice?["message"]?["tool_calls"] is JsonArray toolCalls)
        {
            var n = 0;
            foreach (var tc in toolCalls)
            {
                n++;
                var name = tc?["function"]?["name"]?.GetValue<string>() ?? "";
                var args = tc?["function"]?["arguments"]?.GetValue<string>() ?? "{}";
                var id = tc?["id"]?.GetValue<string>() ?? $"call_{n}";
                if (name == SubmitTool)
                {
                    final = ParseDraft(args);
                    continue;
                }
                calls.Add(new ToolCall(id, name, Flatten(args), tc?["extra_content"]?.ToJsonString()));
            }
        }
        return new ModelReply(final is null ? calls : [], final, null, input, output);
    }

    static Dictionary<string, string> Flatten(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (JsonNode.Parse(json) is not JsonObject o) return result;
        foreach (var (k, v) in o)
            if (v is not null) result[k] = v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v.ToJsonString();
        return result;
    }

    static string Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToJsonString() ?? "";

    public static DiagnosisDraft ParseDraft(string json)
    {
        var o = JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException("The diagnosis is not a JSON object");
        var confidence = Text(o["confidence"]).ToLowerInvariant() switch { "high" => Confidence.High, "medium" => Confidence.Medium, _ => Confidence.Low };
        var evidence = (o["evidence"] as JsonArray ?? []).OfType<JsonObject>().Select(e => new Claim(Text(e["statement"]),
            (e["sources"] as JsonArray ?? []).Select(Text).Where(s => s.Length > 0).ToList())).ToList();
        var alternatives = (o["alternatives"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(a => new RejectedHypothesis(Text(a["hypothesis"]), Text(a["why"]))).ToList();
        var options = (o["options"] as JsonArray ?? []).OfType<JsonObject>().Select(x =>
        {
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            switch (x["parameters"])
            {
                case JsonArray list:
                    foreach (var p in list.OfType<JsonObject>()) parameters[Text(p["name"])] = Text(p["value"]);
                    break;
                case JsonObject map:
                    foreach (var (k, v) in map) parameters[k] = Text(v);
                    break;
            }
            return new ProposedOption(Text(x["action_id"]), parameters, Text(x["rationale"]));
        }).ToList();
        return new DiagnosisDraft(Text(o["summary"]), Text(o["root_cause"]), confidence, evidence, alternatives, options);
    }

    public void Dispose() => _http.Dispose();
}
