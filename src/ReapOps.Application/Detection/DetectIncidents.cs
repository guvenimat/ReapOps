using System.Globalization;
using ReapOps.Application.Ports;
using ReapOps.Domain.Common;
using ReapOps.Domain.Incidents;
using ReapOps.Domain.Slos;

namespace ReapOps.Application.Detection;

public sealed record DetectionResult(int Conditions, int Opened, int Resolved, int Notified, IReadOnlyList<string> Unavailable);

public sealed class DetectIncidents(IMetricsReader metrics, IClusterReader cluster, IIncidentReader reader, IIncidentStore store, INotifier notifier,
    IClock clock, MonitoringOptions options)
{
    public const string SignalUnavailable = "signal_unavailable";

    public async Task<DetectionResult> RunAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var conditions = new List<Condition>();
        var evaluated = new HashSet<(string Service, string Kind)>();
        var unavailable = new List<string>();

        foreach (var service in options.Services)
        {
            var failed = false;
            foreach (var slo in service.Slos)
            {
                var kind = $"slo_burn:{slo.Name}";
                var (condition, failure) = await EvaluateSloAsync(service.Name, slo, ct);
                if (failure is not null)
                {
                    failed = true;
                    unavailable.Add($"{service.Name} metrics");
                    conditions.Add(new Condition(new Fingerprint(service.Name, SignalUnavailable, "metrics"), Severity.Warning,
                        $"Cannot read metrics for {service.Name}", [new Evidence("Error", failure)]));
                    continue;
                }
                evaluated.Add((service.Name, kind));
                if (condition is not null) conditions.Add(condition);
            }

            if (service.Namespace is { Length: > 0 } ns)
            {
                try
                {
                    var snapshot = await cluster.SnapshotAsync(ns, ct);
                    conditions.AddRange(WorkloadRules.Evaluate(service.Name, snapshot));
                    foreach (var kind in new[] { "autoscaler_at_max", "crash_loop", "oom_killed", "restarts" }) evaluated.Add((service.Name, kind));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
                {
                    failed = true;
                    unavailable.Add($"{service.Name} cluster");
                    conditions.Add(new Condition(new Fingerprint(service.Name, SignalUnavailable, "cluster"), Severity.Warning,
                        $"Cannot read the cluster state for {service.Name}", [new Evidence("Error", ex.Message)]));
                }
            }
            if (!failed) evaluated.Add((service.Name, SignalUnavailable));
        }

        var active = (await reader.ActiveAsync(ct)).ToDictionary(i => i.Fingerprint);
        var touched = new List<Incident>();
        var opened = 0;
        foreach (var condition in conditions.GroupBy(c => c.Fingerprint).Select(g => g.MaxBy(c => c.Severity)!))
        {
            if (active.Remove(condition.Fingerprint, out var incident)) incident.Observe(condition, now);
            else
            {
                incident = Incident.Open(Guid.CreateVersion7(now), condition, now);
                opened++;
            }
            touched.Add(incident);
        }

        var resolved = 0;
        foreach (var incident in active.Values)
        {
            if (!evaluated.Contains((incident.Fingerprint.Service, incident.Fingerprint.Kind))) continue;
            if (incident.Clear(now)) resolved++;
            touched.Add(incident);
        }

        var windows = await reader.MaintenanceWindowsAsync(now, ct);
        var sent = await reader.NotificationsSinceAsync(now.AddHours(-1), ct);
        var notified = 0;
        foreach (var incident in touched)
        {
            var events = incident.DomainEvents.ToList();
            await store.SaveAsync(incident, ct);
            incident.ClearDomainEvents();
            foreach (var e in events)
            {
                var decision = NoisePolicy.Decide(e, incident.Fingerprint.Service, windows, sent, now);
                if (decision == NotifyDecision.Ignored) continue;
                var kind = Kind(e);
                if (decision == NotifyDecision.Send)
                {
                    await notifier.NotifyAsync(Compose(incident, e, kind), ct);
                    sent++;
                    notified++;
                }
                await store.RecordNotificationAsync(incident.Id, kind, decision, now, ct);
            }
        }
        return new DetectionResult(conditions.Count, opened, resolved, notified, unavailable);
    }

    async Task<(Condition? Condition, string? Failure)> EvaluateSloAsync(string service, SloDefinition definition, CancellationToken ct)
    {
        var slo = Slo.Create(definition.Name, definition.Objective);
        var readings = new List<WindowReading>();
        foreach (var window in BurnRatePolicy.Windows(BurnRatePolicy.Default))
        {
            double? ratio;
            try
            {
                ratio = await metrics.ScalarAsync(PromWindow.Render(definition.ErrorRatioQuery, window), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                return (null, ex.Message);
            }
            readings.Add(new WindowReading(window, ratio is { } r && double.IsFinite(r) ? r : 0));
        }
        var alert = BurnRatePolicy.Evaluate(slo, readings);
        if (alert is null) return (null, null);
        var rule = alert.Rule;
        var evidence = new List<Evidence>
        {
            new("Objective", PromWindow.Percent(slo.Objective)),
            new($"Burn rate over {PromWindow.Format(rule.Long)}", alert.LongBurn.ToString("0.0", CultureInfo.InvariantCulture) + "x",
                PromWindow.Render(definition.ErrorRatioQuery, rule.Long)),
            new($"Burn rate over {PromWindow.Format(rule.Short)}", alert.ShortBurn.ToString("0.0", CultureInfo.InvariantCulture) + "x",
                PromWindow.Render(definition.ErrorRatioQuery, rule.Short)),
            new("Threshold", rule.Threshold.ToString("0.#", CultureInfo.InvariantCulture) + "x"),
        };
        evidence.AddRange(readings.Select(r => new Evidence($"Error ratio {PromWindow.Format(r.Window)}", PromWindow.Percent(r.ErrorRatio))));
        return (new Condition(new Fingerprint(service, $"slo_burn:{slo.Name}", slo.Name), alert.Severity,
            $"{service}: {slo.Name} SLO is burning its error budget {alert.ShortBurn:0.0}x too fast", evidence), null);
    }

    static string Kind(IDomainEvent e) => e switch
    {
        IncidentOpened => "opened",
        IncidentEscalated => "escalated",
        IncidentResolved => "resolved",
        _ => "other",
    };

    Notification Compose(Incident incident, IDomainEvent e, string kind)
    {
        var url = $"{options.ConsoleUrl.TrimEnd('/')}/#/incidents/{incident.Id}";
        var prefix = kind switch
        {
            "opened" => $"[{incident.Severity}] ",
            "escalated" => $"[Escalated to {incident.Severity}] ",
            _ => "[Resolved] ",
        };
        var lines = string.Join("\n", incident.Evidence.Select(ev => $"  {ev.Label}: {ev.Value}"));
        var detail = e is IncidentResolved r ? $"Resolved: {r.Reason}." : $"Evidence:\n{lines}";
        return new Notification(incident.Id, kind, incident.Severity, incident.Fingerprint.Service, prefix + incident.Title,
            $"{incident.Title}\n\n{detail}\n\nOpen the incident: {url}\n");
    }
}
