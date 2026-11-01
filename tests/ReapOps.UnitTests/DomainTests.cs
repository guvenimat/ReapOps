using ReapOps.Domain.Common;
using ReapOps.Domain.Incidents;
using ReapOps.Domain.Slos;

namespace ReapOps.UnitTests;

public class BurnRateTests
{
    static readonly Slo Availability = Slo.Create("availability", 0.995);

    static List<WindowReading> Readings(double longRatio, double shortRatio) =>
    [
        new(TimeSpan.FromHours(1), longRatio),
        new(TimeSpan.FromMinutes(5), shortRatio),
        new(TimeSpan.FromHours(6), 0),
        new(TimeSpan.FromMinutes(30), 0),
        new(TimeSpan.FromDays(1), 0),
        new(TimeSpan.FromHours(2), 0),
    ];

    [Fact]
    public void Burn_rate_is_the_error_ratio_over_the_budget() =>
        Assert.Equal(10, Availability.BurnRate(0.05), 6);

    [Fact]
    public void A_fast_burn_in_both_windows_pages()
    {
        var alert = BurnRatePolicy.Evaluate(Availability, Readings(0.08, 0.1));
        Assert.NotNull(alert);
        Assert.Equal(Severity.Critical, alert.Severity);
        Assert.Equal(16, alert.LongBurn, 6);
    }

    [Fact]
    public void A_short_spike_alone_does_not_alert() =>
        Assert.Null(BurnRatePolicy.Evaluate(Availability, Readings(0.01, 0.5)));

    [Fact]
    public void A_burn_that_has_already_stopped_does_not_alert() =>
        Assert.Null(BurnRatePolicy.Evaluate(Availability, Readings(0.5, 0.001)));

    [Fact]
    public void Slow_burns_raise_a_warning()
    {
        var readings = new List<WindowReading>
        {
            new(TimeSpan.FromDays(1), 0.02), new(TimeSpan.FromHours(2), 0.02),
            new(TimeSpan.FromHours(1), 0.02), new(TimeSpan.FromMinutes(5), 0.02),
            new(TimeSpan.FromHours(6), 0.02), new(TimeSpan.FromMinutes(30), 0.02),
        };
        Assert.Equal(Severity.Warning, BurnRatePolicy.Evaluate(Availability, readings)?.Severity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1.5)]
    public void Objectives_must_be_a_fraction(double objective) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Slo.Create("x", objective));
}

public class IncidentTests
{
    static readonly DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
    static readonly Fingerprint Key = new("shop", "slo_burn:availability", "availability");

    static Condition Cond(Severity s) => new(Key, s, "shop availability burning", [new Evidence("Burn", "16x")]);

    [Fact]
    public void Opening_raises_an_event()
    {
        var i = Incident.Open(Guid.NewGuid(), Cond(Severity.Warning), Now);
        Assert.Equal(IncidentStatus.Open, i.Status);
        Assert.IsType<IncidentOpened>(Assert.Single(i.DomainEvents));
    }

    [Fact]
    public void A_worse_reading_escalates_once_and_a_better_one_does_not_downgrade()
    {
        var i = Incident.Open(Guid.NewGuid(), Cond(Severity.Warning), Now);
        i.ClearDomainEvents();
        i.Observe(Cond(Severity.Critical), Now.AddMinutes(1));
        i.Observe(Cond(Severity.Warning), Now.AddMinutes(2));
        Assert.Equal(Severity.Critical, i.Severity);
        Assert.IsType<IncidentEscalated>(Assert.Single(i.DomainEvents));
    }

    [Fact]
    public void An_incident_resolves_only_after_the_condition_stays_clear()
    {
        var i = Incident.Open(Guid.NewGuid(), Cond(Severity.Warning), Now);
        Assert.False(i.Clear(Now.AddMinutes(1)));
        i.Observe(Cond(Severity.Warning), Now.AddMinutes(5));
        Assert.False(i.Clear(Now.AddMinutes(6)));
        Assert.False(i.Clear(Now.AddMinutes(15)));
        Assert.True(i.Clear(Now.AddMinutes(16)));
        Assert.Equal(IncidentStatus.Resolved, i.Status);
        Assert.Equal("recovered", i.Resolution);
    }

    [Fact]
    public void Acknowledging_is_idempotent_and_resolved_incidents_are_final()
    {
        var i = Incident.Open(Guid.NewGuid(), Cond(Severity.Warning), Now);
        i.ClearDomainEvents();
        i.Acknowledge("Ayse", Now);
        i.Acknowledge("Ayse", Now);
        Assert.Single(i.DomainEvents);
        i.Resolve("Ayse", Now);
        Assert.Equal("incident.resolved", Assert.Throws<DomainException>(() => i.Observe(Cond(Severity.Critical), Now)).Code);
    }

    [Fact]
    public void Fingerprints_round_trip() =>
        Assert.Equal(Key, Fingerprint.Parse(Key.ToString()));
}

public class WorkloadRuleTests
{
    static readonly DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Old_restarts_are_history_and_recent_ones_are_an_incident()
    {
        WorkloadSnapshot Pod(DateTime at) => new([], [new PodState("api-1", "api", null, "Error", 5, at)]);
        Assert.Empty(WorkloadRules.Evaluate("shop", Pod(Now.AddHours(-2)), Now));
        Assert.Equal("restarts", Assert.Single(WorkloadRules.Evaluate("shop", Pod(Now.AddMinutes(-3)), Now)).Fingerprint.Kind);
    }

    [Fact]
    public void An_autoscaler_at_its_ceiling_is_a_warning()
    {
        var snapshot = new WorkloadSnapshot([new AutoscalerState("api", 8, 4, 8, 8), new AutoscalerState("web", 2, 2, 6, 2)], []);
        var c = Assert.Single(WorkloadRules.Evaluate("shop", snapshot, DateTime.UtcNow));
        Assert.Equal("autoscaler_at_max", c.Fingerprint.Kind);
        Assert.Equal(Severity.Warning, c.Severity);
    }

    [Fact]
    public void Crash_loops_are_critical_and_grouped_per_workload()
    {
        var snapshot = new WorkloadSnapshot([],
        [
            new PodState("api-1", "api", "CrashLoopBackOff", "Error", 5),
            new PodState("api-2", "api", "CrashLoopBackOff", "Error", 4),
            new PodState("worker-1", "worker", null, "OOMKilled", 1, Now.AddMinutes(-2)),
            new PodState("web-1", "web", null, null, 0),
            new PodState("old-1", "old", null, "Error", 9, Now.AddHours(-3)),
        ]);
        var conditions = WorkloadRules.Evaluate("shop", snapshot, Now).ToList();
        var crash = Assert.Single(conditions, c => c.Fingerprint.Kind == "crash_loop");
        Assert.Equal(Severity.Critical, crash.Severity);
        Assert.Equal(2, crash.Evidence.Count);
        Assert.Single(conditions, c => c.Fingerprint.Kind == "oom_killed" && c.Fingerprint.Key == "worker");
        Assert.Equal(2, conditions.Count);
    }
}

public class NoisePolicyTests
{
    static readonly DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
    static readonly IncidentOpened Opened = new(Guid.NewGuid(), new Fingerprint("shop", "x", "y"), Severity.Critical, "t", Now);

    [Fact]
    public void Maintenance_windows_silence_their_service_only()
    {
        var windows = new[] { new MaintenanceWindow("shop", Now.AddMinutes(-5), Now.AddMinutes(30), "deploy") };
        Assert.Equal(NotifyDecision.Maintenance, NoisePolicy.Decide(Opened, "shop", windows, 0, Now));
        Assert.Equal(NotifyDecision.Send, NoisePolicy.Decide(Opened, "other", windows, 0, Now));
    }

    [Fact]
    public void The_hourly_cap_never_hides_a_resolution()
    {
        Assert.Equal(NotifyDecision.RateLimited, NoisePolicy.Decide(Opened, "shop", [], NoisePolicy.MaxNotificationsPerHour, Now));
        var resolved = new IncidentResolved(Guid.NewGuid(), Opened.Fingerprint, "recovered", "t", Now);
        Assert.Equal(NotifyDecision.Send, NoisePolicy.Decide(resolved, "shop", [], NoisePolicy.MaxNotificationsPerHour, Now));
    }
}
