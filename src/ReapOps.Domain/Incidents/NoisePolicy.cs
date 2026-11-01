namespace ReapOps.Domain.Incidents;

public sealed record MaintenanceWindow(string Service, DateTime StartsAt, DateTime EndsAt, string Reason)
{
    public bool Covers(string service, DateTime at) =>
        (Service == "*" || string.Equals(Service, service, StringComparison.OrdinalIgnoreCase)) && at >= StartsAt && at < EndsAt;
}

public enum NotifyDecision { Send, Maintenance, RateLimited, Ignored }

public static class NoisePolicy
{
    public const int MaxNotificationsPerHour = 20;

    public static NotifyDecision Decide(Common.IDomainEvent e, string service, IReadOnlyCollection<MaintenanceWindow> windows, int sentLastHour, DateTime now)
    {
        if (e is IncidentAcknowledged) return NotifyDecision.Ignored;
        if (windows.Any(w => w.Covers(service, now))) return NotifyDecision.Maintenance;
        if (sentLastHour >= MaxNotificationsPerHour && e is not IncidentResolved) return NotifyDecision.RateLimited;
        return NotifyDecision.Send;
    }
}
