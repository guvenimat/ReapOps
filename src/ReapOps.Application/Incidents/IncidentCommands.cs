using ReapOps.Application.Ports;
using ReapOps.Domain.Common;
using ReapOps.Domain.Incidents;

namespace ReapOps.Application.Incidents;

public sealed record IncidentDetail(IncidentSummary Summary, IReadOnlyList<Evidence> Evidence, string? AcknowledgedBy, DateTime? AcknowledgedAt,
    string? Resolution, IReadOnlyList<TimelineEntry> Timeline);

public sealed class IncidentQueries(IIncidentReader reader)
{
    public Task<IReadOnlyList<IncidentSummary>> ListAsync(IncidentStatus? status, CancellationToken ct) => reader.ListAsync(status, 200, ct);

    public async Task<IncidentDetail?> DetailAsync(Guid id, CancellationToken ct)
    {
        if (await reader.GetAsync(id, ct) is not { } i) return null;
        return new IncidentDetail(
            new IncidentSummary(i.Id, i.Fingerprint.ToString(), i.Fingerprint.Service, i.Title, i.Severity, i.Status, i.OpenedAt, i.LastSeenAt, i.ResolvedAt),
            i.Evidence, i.AcknowledgedBy, i.AcknowledgedAt, i.Resolution, await reader.TimelineAsync(id, ct));
    }
}

public enum IncidentChange { Changed, NotFound }

public sealed class AcknowledgeIncident(IIncidentReader reader, IIncidentStore store, IClock clock)
{
    public async Task<IncidentChange> RunAsync(Guid id, string by, CancellationToken ct)
    {
        if (await reader.GetAsync(id, ct) is not { } incident) return IncidentChange.NotFound;
        incident.Acknowledge(by, clock.UtcNow);
        await store.SaveAsync(incident, ct);
        return IncidentChange.Changed;
    }
}

public sealed class ResolveIncident(IIncidentReader reader, IIncidentStore store, IClock clock)
{
    public async Task<IncidentChange> RunAsync(Guid id, string by, CancellationToken ct)
    {
        if (await reader.GetAsync(id, ct) is not { } incident) return IncidentChange.NotFound;
        if (!incident.IsActive) throw new DomainException("incident.resolved", "The incident is already resolved");
        incident.Resolve(by, clock.UtcNow);
        await store.SaveAsync(incident, ct);
        return IncidentChange.Changed;
    }
}
