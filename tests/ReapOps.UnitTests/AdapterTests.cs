using Microsoft.Extensions.Configuration;
using ReapOps.Api;
using ReapOps.Application.Detection;
using ReapOps.Infrastructure.Signals;

namespace ReapOps.UnitTests;

public class AdapterTests
{
    [Fact]
    public void Prometheus_vectors_and_scalars_are_read()
    {
        Assert.Equal(0.0125, PrometheusReader.Parse("""{"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1793519363.6,"0.0125"]}]}}"""));
        Assert.Equal(2, PrometheusReader.Parse("""{"status":"success","data":{"resultType":"scalar","result":[1793519363.6,"2"]}}"""));
        Assert.Null(PrometheusReader.Parse("""{"status":"success","data":{"resultType":"vector","result":[]}}"""));
        Assert.Null(PrometheusReader.Parse("""{"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1,"NaN"]}]}}"""));
    }

    [Fact]
    public void Kubernetes_autoscalers_and_pods_are_read()
    {
        var hpas = KubernetesReader.ParseAutoscalers("""
            {"items":[{"metadata":{"name":"api"},"spec":{"minReplicas":4,"maxReplicas":8},"status":{"currentReplicas":8,"desiredReplicas":9}}]}
            """);
        Assert.Equal(new Domain.Incidents.AutoscalerState("api", 8, 4, 8, 9), Assert.Single(hpas));

        var pods = KubernetesReader.ParsePods("""
            {"items":[
              {"metadata":{"name":"api-1","labels":{"app.kubernetes.io/name":"api"}},"status":{"containerStatuses":[
                {"restartCount":4,"state":{"waiting":{"reason":"CrashLoopBackOff"}},"lastState":{"terminated":{"reason":"Error"}}}]}},
              {"metadata":{"name":"pending-1"},"status":{}}
            ]}
            """);
        Assert.Equal(new Domain.Incidents.PodState("api-1", "api", "CrashLoopBackOff", "Error", 4), Assert.Single(pods));
    }

    [Theory]
    [InlineData(5, "5m")]
    [InlineData(60, "1h")]
    [InlineData(1440, "1d")]
    [InlineData(90, "90m")]
    public void Prometheus_windows_are_formatted(int minutes, string expected) =>
        Assert.Equal(expected, PromWindow.Format(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void Operator_sessions_are_signed_and_expire()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Console:SigningKey"] = "test-signing-key-0123456789",
            ["Console:Password"] = "correct horse",
        }).Build();
        var sessions = new OperatorSessions(cfg);
        var now = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var cookie = sessions.Issue("Ayse Y.", now);

        Assert.Equal("Ayse Y.", sessions.Read(cookie, now.AddHours(1))?.Name);
        Assert.Null(sessions.Read(cookie, now.AddHours(13)));
        Assert.Null(sessions.Read(cookie.Replace("|", "x|", StringComparison.Ordinal), now));
        Assert.Null(sessions.Read(Convert.ToBase64String("Mallory"u8.ToArray()) + cookie[cookie.IndexOf('|', StringComparison.Ordinal)..], now));
        Assert.True(sessions.CheckPassword("correct horse"));
        Assert.False(sessions.CheckPassword("wrong"));
    }
}

public class ApiContractTests
{
    [Fact]
    public void Enums_are_sent_in_snake_case()
    {
        var summary = new Application.Ports.IncidentSummary(Guid.Empty, "a/b/c", "a", "t", Domain.Incidents.Severity.Critical,
            Domain.Incidents.IncidentStatus.Acknowledged, DateTime.UnixEpoch, DateTime.UnixEpoch, null);
        var json = System.Text.Json.JsonSerializer.Serialize(new IncidentListView([summary]), ApiJsonContext.Default.IncidentListView);
        Assert.Contains("\"severity\":\"critical\"", json);
        Assert.Contains("\"status\":\"acknowledged\"", json);
    }
}
