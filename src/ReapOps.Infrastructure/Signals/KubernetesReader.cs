using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using ReapOps.Application.Ports;
using ReapOps.Domain.Incidents;

namespace ReapOps.Infrastructure.Signals;

public sealed class KubernetesReader : IClusterReader, IDisposable
{
    const string ServiceAccount = "/var/run/secrets/kubernetes.io/serviceaccount";
    readonly HttpClient? _http;
    readonly string _tokenPath;

    public KubernetesReader(IConfiguration cfg)
    {
        _tokenPath = cfg["Kubernetes:TokenPath"] ?? Path.Combine(ServiceAccount, "token");
        var caPath = cfg["Kubernetes:CaPath"] ?? Path.Combine(ServiceAccount, "ca.crt");
        var host = cfg["Kubernetes:Url"] ?? (Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST") is { Length: > 0 } h
            ? $"https://{h}:{Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT")}" : null);
        if (host is null || !File.Exists(_tokenPath)) return;

        var handler = new HttpClientHandler();
        if (File.Exists(caPath))
        {
            var ca = X509CertificateLoader.LoadCertificateFromFile(caPath);
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            {
                if (cert is null) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(ca);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(cert);
            };
        }
        _http = new HttpClient(handler) { BaseAddress = new Uri(host.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<WorkloadSnapshot> SnapshotAsync(string @namespace, CancellationToken ct)
    {
        if (_http is null) throw new InvalidOperationException("Not running in a cluster: no service account token");
        var hpas = await GetAsync($"apis/autoscaling/v2/namespaces/{@namespace}/horizontalpodautoscalers", ct);
        var pods = await GetAsync($"api/v1/namespaces/{@namespace}/pods", ct);
        return new WorkloadSnapshot(ParseAutoscalers(hpas), ParsePods(pods));
    }

    async Task<string> GetAsync(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await File.ReadAllTextAsync(_tokenPath, ct)).Trim());
        using var res = await _http!.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Kubernetes API returned {(int)res.StatusCode} for {path}");
        return body;
    }

    public static IReadOnlyList<AutoscalerState> ParseAutoscalers(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<AutoscalerState>();
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            var spec = item.GetProperty("spec");
            var status = item.GetProperty("status");
            list.Add(new AutoscalerState(
                item.GetProperty("metadata").GetProperty("name").GetString()!,
                Int(status, "currentReplicas"),
                spec.TryGetProperty("minReplicas", out var min) ? min.GetInt32() : 1,
                Int(spec, "maxReplicas"),
                Int(status, "desiredReplicas")));
        }
        return list;
    }

    public static IReadOnlyList<PodState> ParsePods(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<PodState>();
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            var meta = item.GetProperty("metadata");
            var name = meta.GetProperty("name").GetString()!;
            var workload = meta.TryGetProperty("labels", out var labels) && labels.TryGetProperty("app.kubernetes.io/name", out var app)
                ? app.GetString()! : name;
            if (!item.GetProperty("status").TryGetProperty("containerStatuses", out var containers)) continue;
            foreach (var c in containers.EnumerateArray())
            {
                string? waiting = c.TryGetProperty("state", out var state) && state.TryGetProperty("waiting", out var w) && w.TryGetProperty("reason", out var wr)
                    ? wr.GetString() : null;
                string? terminated = c.TryGetProperty("lastState", out var last) && last.TryGetProperty("terminated", out var t) && t.TryGetProperty("reason", out var tr)
                    ? tr.GetString() : null;
                list.Add(new PodState(name, workload, waiting, terminated, Int(c, "restartCount")));
            }
        }
        return list;
    }

    static int Int(JsonElement e, string property) => e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    public void Dispose() => _http?.Dispose();
}
