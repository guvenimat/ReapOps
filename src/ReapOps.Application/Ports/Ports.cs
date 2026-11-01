using ReapOps.Domain.Incidents;

namespace ReapOps.Application.Ports;

public interface IClock
{
    DateTime UtcNow { get; }
}

public interface IMetricsReader
{
    Task<double?> ScalarAsync(string query, DateTime? at, CancellationToken ct);
    Task<IReadOnlyList<string>> MetricNamesAsync(CancellationToken ct);
}

public interface IClusterReader
{
    Task<WorkloadSnapshot> SnapshotAsync(string @namespace, CancellationToken ct);
}

public interface IIncidentReader
{
    Task<IReadOnlyList<Incident>> ActiveAsync(CancellationToken ct);
    Task<Incident?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<IncidentSummary>> ListAsync(IncidentStatus? status, int limit, CancellationToken ct);
    Task<IReadOnlyList<TimelineEntry>> TimelineAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<MaintenanceWindow>> MaintenanceWindowsAsync(DateTime at, CancellationToken ct);
    Task<int> NotificationsSinceAsync(DateTime since, CancellationToken ct);
}

public interface IIncidentStore
{
    Task SaveAsync(Incident incident, CancellationToken ct);
    Task RecordNotificationAsync(Guid incidentId, string kind, NotifyDecision decision, DateTime at, CancellationToken ct);
}

public interface INotifier
{
    Task NotifyAsync(Notification notification, CancellationToken ct);
}

public sealed record Notification(Guid IncidentId, string Kind, Severity Severity, string Service, string Subject, string Body);

public sealed record IncidentSummary(Guid Id, string Fingerprint, string Service, string Title, Severity Severity, IncidentStatus Status,
    DateTime OpenedAt, DateTime LastSeenAt, DateTime? ResolvedAt);

public sealed record TimelineEntry(string Kind, string Detail, DateTime At);
