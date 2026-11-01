using ReapOps.Domain.Common;

namespace ReapOps.Domain.Incidents;

public enum Severity { Info = 0, Warning = 1, Critical = 2 }

public enum IncidentStatus { Open, Acknowledged, Resolved }

public sealed record Fingerprint(string Service, string Kind, string Key)
{
    public override string ToString() => $"{Service}/{Kind}/{Key}";

    public static Fingerprint Parse(string value)
    {
        var parts = value.Split('/', 3);
        if (parts.Length != 3) throw new FormatException($"Not a fingerprint: {value}");
        return new Fingerprint(parts[0], parts[1], parts[2]);
    }
}

public sealed record Evidence(string Label, string Value, string? Query = null);

public sealed record Condition(Fingerprint Fingerprint, Severity Severity, string Title, IReadOnlyList<Evidence> Evidence);

public sealed record IncidentOpened(Guid IncidentId, Fingerprint Fingerprint, Severity Severity, string Title, DateTime OccurredAt) : IDomainEvent;
public sealed record IncidentEscalated(Guid IncidentId, Fingerprint Fingerprint, Severity From, Severity To, string Title, DateTime OccurredAt) : IDomainEvent;
public sealed record IncidentResolved(Guid IncidentId, Fingerprint Fingerprint, string Reason, string Title, DateTime OccurredAt) : IDomainEvent;
public sealed record IncidentAcknowledged(Guid IncidentId, string By, DateTime OccurredAt) : IDomainEvent;

public sealed class Incident : AggregateRoot
{
    public static readonly TimeSpan RecoveryPeriod = TimeSpan.FromMinutes(10);

    Incident(Guid id, Fingerprint fingerprint, string title, Severity severity, IncidentStatus status, DateTime openedAt, DateTime lastSeenAt,
        DateTime? clearedAt, DateTime? acknowledgedAt, string? acknowledgedBy, DateTime? resolvedAt, string? resolution, IReadOnlyList<Evidence> evidence)
    {
        Id = id;
        Fingerprint = fingerprint;
        Title = title;
        Severity = severity;
        Status = status;
        OpenedAt = openedAt;
        LastSeenAt = lastSeenAt;
        ClearedAt = clearedAt;
        AcknowledgedAt = acknowledgedAt;
        AcknowledgedBy = acknowledgedBy;
        ResolvedAt = resolvedAt;
        Resolution = resolution;
        Evidence = evidence;
    }

    public Guid Id { get; }
    public Fingerprint Fingerprint { get; }
    public string Title { get; private set; }
    public Severity Severity { get; private set; }
    public IncidentStatus Status { get; private set; }
    public DateTime OpenedAt { get; }
    public DateTime LastSeenAt { get; private set; }
    public DateTime? ClearedAt { get; private set; }
    public DateTime? AcknowledgedAt { get; private set; }
    public string? AcknowledgedBy { get; private set; }
    public DateTime? ResolvedAt { get; private set; }
    public string? Resolution { get; private set; }
    public IReadOnlyList<Evidence> Evidence { get; private set; }

    public bool IsActive => Status != IncidentStatus.Resolved;

    public static Incident Open(Guid id, Condition condition, DateTime now)
    {
        var incident = new Incident(id, condition.Fingerprint, condition.Title, condition.Severity, IncidentStatus.Open, now, now,
            null, null, null, null, null, condition.Evidence);
        incident.Raise(new IncidentOpened(id, condition.Fingerprint, condition.Severity, condition.Title, now));
        return incident;
    }

    public static Incident Restore(Guid id, Fingerprint fingerprint, string title, Severity severity, IncidentStatus status, DateTime openedAt,
        DateTime lastSeenAt, DateTime? clearedAt, DateTime? acknowledgedAt, string? acknowledgedBy, DateTime? resolvedAt, string? resolution,
        IReadOnlyList<Evidence> evidence) =>
        new(id, fingerprint, title, severity, status, openedAt, lastSeenAt, clearedAt, acknowledgedAt, acknowledgedBy, resolvedAt, resolution, evidence);

    public void Observe(Condition condition, DateTime now)
    {
        EnsureActive();
        if (condition.Fingerprint != Fingerprint) throw new DomainException("incident.fingerprint", "The condition belongs to another incident");
        LastSeenAt = now;
        ClearedAt = null;
        Evidence = condition.Evidence;
        Title = condition.Title;
        if (condition.Severity > Severity)
        {
            var from = Severity;
            Severity = condition.Severity;
            Raise(new IncidentEscalated(Id, Fingerprint, from, Severity, Title, now));
        }
    }

    public bool Clear(DateTime now)
    {
        if (!IsActive) return false;
        ClearedAt ??= now;
        if (now - ClearedAt.Value < RecoveryPeriod) return false;
        Close("recovered", now);
        return true;
    }

    public void Acknowledge(string by, DateTime now)
    {
        EnsureActive();
        if (string.IsNullOrWhiteSpace(by)) throw new DomainException("incident.acknowledged_by", "Who acknowledged the incident is required");
        if (Status == IncidentStatus.Acknowledged) return;
        Status = IncidentStatus.Acknowledged;
        AcknowledgedAt = now;
        AcknowledgedBy = by.Trim();
        Raise(new IncidentAcknowledged(Id, AcknowledgedBy, now));
    }

    public void Resolve(string by, DateTime now)
    {
        EnsureActive();
        if (string.IsNullOrWhiteSpace(by)) throw new DomainException("incident.resolved_by", "Who resolved the incident is required");
        Close($"resolved by {by.Trim()}", now);
    }

    void Close(string resolution, DateTime now)
    {
        Status = IncidentStatus.Resolved;
        ResolvedAt = now;
        Resolution = resolution;
        Raise(new IncidentResolved(Id, Fingerprint, resolution, Title, now));
    }

    void EnsureActive()
    {
        if (!IsActive) throw new DomainException("incident.resolved", "The incident is already resolved");
    }
}
