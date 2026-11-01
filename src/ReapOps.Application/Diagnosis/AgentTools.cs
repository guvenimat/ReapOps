using System.Globalization;
using System.Text;
using ReapOps.Application.Detection;
using ReapOps.Application.Ports;

namespace ReapOps.Application.Diagnosis;

public static class ToolOutput
{
    public const int MaxLength = 4000;

    public static string Untrusted(string tool, string callId, string content)
    {
        var text = Domain.Agent.Redactor.Redact(content);
        if (text.Length > MaxLength) text = text[..MaxLength] + "\n[truncated]";
        return $"<tool_output tool=\"{tool}\" call_id=\"{callId}\">\n{text}\n</tool_output>";
    }

    public static string Arg(IReadOnlyDictionary<string, string> args, string name) =>
        args.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : throw new ArgumentException($"Missing argument '{name}'");
}

public sealed class QueryMetricsTool(IMetricsReader metrics, IClock clock) : IAgentTool
{
    public ToolSpec Spec => Definition;

    public static readonly ToolSpec Definition = new("query_metrics",
        "Evaluate a PromQL expression that returns a single number, now or a number of minutes ago. Use it to compare error rates, latency, traffic and saturation before and during the incident.",
        [
            new ToolParameter("query", "PromQL expression returning one value, for example sum(rate(http_server_request_duration_seconds_count[5m]))"),
            new ToolParameter("minutes_ago", "Optional: evaluate the expression this many minutes in the past", Required: false),
        ]);

    public async Task<string> ExecuteAsync(IReadOnlyDictionary<string, string> arguments, CancellationToken ct)
    {
        var query = ToolOutput.Arg(arguments, "query");
        DateTime? at = arguments.TryGetValue("minutes_ago", out var m) && int.TryParse(m, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes > 0
            ? clock.UtcNow.AddMinutes(-Math.Min(minutes, 60 * 24 * 8)) : null;
        var value = await metrics.ScalarAsync(query, at, ct);
        return value is { } v ? v.ToString("0.######", CultureInfo.InvariantCulture) : "no data";
    }
}

public sealed class ListMetricsTool(IMetricsReader metrics) : IAgentTool
{
    public const int MaxNames = 150;

    public ToolSpec Spec => Definition;

    public static readonly ToolSpec Definition = new("list_metrics",
        "List the metric names that exist in Prometheus, optionally only those containing a word. Use it before writing a query so that you query metrics that exist.",
        [new ToolParameter("contains", "Optional word that the metric name must contain, for example http, orders, memory or db", Required: false)]);

    public async Task<string> ExecuteAsync(IReadOnlyDictionary<string, string> arguments, CancellationToken ct)
    {
        var names = await metrics.MetricNamesAsync(ct);
        if (arguments.TryGetValue("contains", out var word) && !string.IsNullOrWhiteSpace(word))
            names = names.Where(n => n.Contains(word.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var shown = names.Take(MaxNames).ToList();
        return shown.Count == 0 ? "no metrics" : string.Join("\n", shown) + (names.Count > shown.Count ? $"\n... {names.Count - shown.Count} more; narrow with contains" : "");
    }
}

public sealed class WorkloadStatusTool(IClusterReader cluster, MonitoringOptions monitoring) : IAgentTool
{
    public ToolSpec Spec => Definition;

    public static readonly ToolSpec Definition = new("workload_status",
        "List the autoscalers and pod states (waiting reason, last termination reason, restarts) of a watched namespace.",
        [new ToolParameter("namespace", "A namespace that ReapOps watches")]);

    public async Task<string> ExecuteAsync(IReadOnlyDictionary<string, string> arguments, CancellationToken ct)
    {
        var ns = ToolOutput.Arg(arguments, "namespace");
        if (!monitoring.Services.Any(s => s.Namespace == ns)) return $"Namespace '{ns}' is not watched; allowed: {string.Join(", ", monitoring.Services.Select(s => s.Namespace).OfType<string>().Distinct())}";
        var snapshot = await cluster.SnapshotAsync(ns, ct);
        var sb = new StringBuilder();
        foreach (var h in snapshot.Autoscalers) sb.AppendLine(CultureInfo.InvariantCulture, $"autoscaler {h.Name}: {h.Current} replicas (min {h.Min}, max {h.Max}, desired {h.Desired})");
        foreach (var p in snapshot.Pods)
            sb.AppendLine(CultureInfo.InvariantCulture, $"pod {p.Name} ({p.Workload}): restarts {p.Restarts}, waiting {p.WaitingReason ?? "-"}, last exit {p.LastTerminationReason ?? "-"} at {p.LastTerminatedAt?.ToString("u", CultureInfo.InvariantCulture) ?? "-"}");
        return sb.Length == 0 ? "no workloads" : sb.ToString();
    }
}

public sealed class IncidentHistoryTool(IIncidentReader incidents) : IAgentTool
{
    public ToolSpec Spec => Definition;

    public static readonly ToolSpec Definition = new("incident_history",
        "List the recent incidents of a service, including resolved ones, to see whether this has happened before.",
        [new ToolParameter("service", "Service name as shown in the incident")]);

    public async Task<string> ExecuteAsync(IReadOnlyDictionary<string, string> arguments, CancellationToken ct)
    {
        var service = ToolOutput.Arg(arguments, "service");
        var rows = (await incidents.ListAsync(null, 200, ct)).Where(i => i.Service == service).Take(20).ToList();
        return rows.Count == 0 ? "no incidents" : string.Join("\n", rows.Select(i =>
            $"{i.OpenedAt.ToString("u", CultureInfo.InvariantCulture)} {i.Severity} {i.Status}: {i.Title}"));
    }
}
