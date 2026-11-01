using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using ReapOps.Application.Ports;

namespace ReapOps.Infrastructure.Signals;

public sealed class PrometheusReader : IMetricsReader, IDisposable
{
    readonly HttpClient _http;

    public PrometheusReader(IConfiguration cfg)
    {
        var url = cfg["Prometheus:Url"] ?? throw new InvalidOperationException("Prometheus:Url is not set");
        _http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<double?> ScalarAsync(string query, CancellationToken ct)
    {
        using var res = await _http.GetAsync("api/v1/query?query=" + Uri.EscapeDataString(query), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Prometheus returned {(int)res.StatusCode}: {Truncate(body)}");
        return Parse(body);
    }

    public static double? Parse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");
        var result = data.GetProperty("result");
        JsonElement value;
        switch (data.GetProperty("resultType").GetString())
        {
            case "scalar":
                value = result;
                break;
            case "vector" when result.GetArrayLength() > 0:
                value = result[0].GetProperty("value");
                break;
            default:
                return null;
        }
        var text = value[1].GetString();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
    }

    static string Truncate(string s) => s.Length > 300 ? s[..300] : s;

    public void Dispose() => _http.Dispose();
}
