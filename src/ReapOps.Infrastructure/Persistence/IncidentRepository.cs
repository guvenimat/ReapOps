using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Npgsql;
using ReapOps.Application.Ports;
using ReapOps.Domain.Common;
using ReapOps.Domain.Incidents;

namespace ReapOps.Infrastructure.Persistence;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(List<Evidence>))]
internal sealed partial class PersistenceJsonContext : JsonSerializerContext;

public sealed class IncidentRepository(NpgsqlDataSource db) : IIncidentReader, IIncidentStore
{
    public sealed record IncidentRecord(
        Guid Id, string Fingerprint, string Service, string Kind, string Title, string Severity, string Status, DateTime OpenedAt, DateTime LastSeenAt,
        DateTime? ClearedAt, DateTime? AcknowledgedAt, string? AcknowledgedBy, DateTime? ResolvedAt, string? Resolution, string Evidence);
    public sealed record IdRecord(Guid Id);
    public sealed record StatusRecord(string? Status, int Limit);
    public sealed record AtRecord(DateTime At);
    public sealed record EventRecord(Guid IncidentId, string Kind, string Detail, DateTime At);
    public sealed record NotificationRecord(Guid IncidentId, string Kind, string Decision, DateTime At);
    public sealed record SummaryRecord(Guid Id, string Fingerprint, string Service, string Title, string Severity, string Status, DateTime OpenedAt,
        DateTime LastSeenAt, DateTime? ResolvedAt);
    public sealed record WindowRecord(string Service, DateTime StartsAt, DateTime EndsAt, string Reason);
    public sealed record TimelineRecord(string Kind, string Detail, DateTime At);

    const string Columns = """
        id, fingerprint, service, kind, title, severity, status, opened_at, last_seen_at, cleared_at, acknowledged_at, acknowledged_by,
        resolved_at, resolution, evidence::text AS evidence
        """;

    public async Task<IReadOnlyList<Incident>> ActiveAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<IncidentRecord>($"SELECT {Columns} FROM incidents WHERE status <> 'resolved'")).Select(Restore).ToList();
    }

    public async Task<Incident?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<IncidentRecord>($"SELECT {Columns} FROM incidents WHERE id = @Id", new IdRecord(id));
        return row is null ? null : Restore(row);
    }

    public async Task<IReadOnlyList<IncidentSummary>> ListAsync(IncidentStatus? status, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SummaryRecord>("""
            SELECT id, fingerprint, service, title, severity, status, opened_at, last_seen_at, resolved_at FROM incidents
            WHERE @Status::text IS NULL OR status = @Status
            ORDER BY (status = 'resolved'), CASE severity WHEN 'critical' THEN 0 WHEN 'warning' THEN 1 ELSE 2 END, opened_at DESC
            LIMIT @Limit
            """, new StatusRecord(status is null ? null : Name(status.Value), limit));
        return rows.Select(r => new IncidentSummary(r.Id, r.Fingerprint, r.Service, r.Title, ParseSeverity(r.Severity), ParseStatus(r.Status),
            r.OpenedAt, r.LastSeenAt, r.ResolvedAt)).ToList();
    }

    public async Task<IReadOnlyList<TimelineEntry>> TimelineAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<TimelineRecord>("""
            SELECT kind, detail, at FROM incident_events WHERE incident_id = @Id
            UNION ALL SELECT 'notification_' || decision, kind, at FROM notifications WHERE incident_id = @Id
            ORDER BY at
            """, new IdRecord(id))).Select(r => new TimelineEntry(r.Kind, r.Detail, r.At)).ToList();
    }

    public async Task<IReadOnlyList<MaintenanceWindow>> MaintenanceWindowsAsync(DateTime at, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<WindowRecord>("SELECT service, starts_at, ends_at, reason FROM maintenance_windows WHERE ends_at > @At",
            new AtRecord(at))).Select(w => new MaintenanceWindow(w.Service, w.StartsAt, w.EndsAt, w.Reason)).ToList();
    }

    public async Task<int> NotificationsSinceAsync(DateTime since, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM notifications WHERE decision = 'send' AND at >= @At", new AtRecord(since));
    }

    public async Task SaveAsync(Incident i, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO incidents (id, fingerprint, service, kind, title, severity, status, opened_at, last_seen_at, cleared_at, acknowledged_at,
                acknowledged_by, resolved_at, resolution, evidence)
            VALUES (@Id, @Fingerprint, @Service, @Kind, @Title, @Severity, @Status, @OpenedAt, @LastSeenAt, @ClearedAt, @AcknowledgedAt,
                @AcknowledgedBy, @ResolvedAt, @Resolution, @Evidence::jsonb)
            ON CONFLICT (id) DO UPDATE SET title = excluded.title, severity = excluded.severity, status = excluded.status,
                last_seen_at = excluded.last_seen_at, cleared_at = excluded.cleared_at, acknowledged_at = excluded.acknowledged_at,
                acknowledged_by = excluded.acknowledged_by, resolved_at = excluded.resolved_at, resolution = excluded.resolution,
                evidence = excluded.evidence, updated_at = now()
            """, new IncidentRecord(i.Id, i.Fingerprint.ToString(), i.Fingerprint.Service, i.Fingerprint.Kind, i.Title, Name(i.Severity), Name(i.Status),
            i.OpenedAt, i.LastSeenAt, i.ClearedAt, i.AcknowledgedAt, i.AcknowledgedBy, i.ResolvedAt, i.Resolution,
            JsonSerializer.Serialize(i.Evidence.ToList(), PersistenceJsonContext.Default.ListEvidence)), tx);
        foreach (var e in i.DomainEvents)
            await conn.ExecuteAsync("INSERT INTO incident_events (incident_id, kind, detail, at) VALUES (@IncidentId, @Kind, @Detail, @At)",
                Describe(i.Id, e), tx);
        await tx.CommitAsync(ct);
    }

    public async Task RecordNotificationAsync(Guid incidentId, string kind, NotifyDecision decision, DateTime at, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("INSERT INTO notifications (incident_id, kind, decision, at) VALUES (@IncidentId, @Kind, @Decision, @At)",
            new NotificationRecord(incidentId, kind, decision switch
            {
                NotifyDecision.Send => "send",
                NotifyDecision.Maintenance => "maintenance",
                NotifyDecision.RateLimited => "rate_limited",
                _ => "ignored",
            }, at));
    }

    static EventRecord Describe(Guid id, IDomainEvent e) => e switch
    {
        IncidentOpened o => new EventRecord(id, "opened", $"Opened as {Name(o.Severity)}: {o.Title}", o.OccurredAt),
        IncidentEscalated x => new EventRecord(id, "escalated", $"Escalated from {Name(x.From)} to {Name(x.To)}", x.OccurredAt),
        IncidentAcknowledged a => new EventRecord(id, "acknowledged", $"Acknowledged by {a.By}", a.OccurredAt),
        IncidentResolved r => new EventRecord(id, "resolved", $"Resolved: {r.Reason}", r.OccurredAt),
        _ => new EventRecord(id, "event", e.GetType().Name, e.OccurredAt),
    };

    static Incident Restore(IncidentRecord r) => Incident.Restore(r.Id, Fingerprint.Parse(r.Fingerprint), r.Title, ParseSeverity(r.Severity),
        ParseStatus(r.Status), r.OpenedAt, r.LastSeenAt, r.ClearedAt, r.AcknowledgedAt, r.AcknowledgedBy, r.ResolvedAt, r.Resolution,
        JsonSerializer.Deserialize(r.Evidence, PersistenceJsonContext.Default.ListEvidence) ?? []);

    public static string Name(Severity s) => s switch { Severity.Critical => "critical", Severity.Warning => "warning", _ => "info" };

    public static string Name(IncidentStatus s) => s switch { IncidentStatus.Open => "open", IncidentStatus.Acknowledged => "acknowledged", _ => "resolved" };

    public static Severity ParseSeverity(string s) => s switch { "critical" => Severity.Critical, "warning" => Severity.Warning, _ => Severity.Info };

    public static IncidentStatus ParseStatus(string s) => s switch { "open" => IncidentStatus.Open, "acknowledged" => IncidentStatus.Acknowledged, _ => IncidentStatus.Resolved };
}
