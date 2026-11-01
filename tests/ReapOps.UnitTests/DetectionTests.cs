using ReapOps.Application.Detection;
using ReapOps.Application.Ports;
using ReapOps.Domain.Incidents;

namespace ReapOps.UnitTests;

public class DetectionTests
{
    sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
    }

    sealed class FakeMetrics : IMetricsReader
    {
        public double Ratio { get; set; }
        public bool Down { get; set; }
        public Func<string, DateTime?, double?>? Series { get; set; }

        public Task<double?> ScalarAsync(string query, DateTime? at, CancellationToken ct) =>
            Down ? throw new HttpRequestException("connection refused")
            : Task.FromResult(Series is not null && query.StartsWith("orders", StringComparison.Ordinal) ? Series(query, at) : Ratio);
    }

    sealed class FakeCluster : IClusterReader
    {
        public WorkloadSnapshot Snapshot { get; set; } = new([], []);

        public Task<WorkloadSnapshot> SnapshotAsync(string @namespace, CancellationToken ct) => Task.FromResult(Snapshot);
    }

    sealed class FakeStore : IIncidentReader, IIncidentStore
    {
        public readonly Dictionary<Guid, Incident> Incidents = [];
        public readonly List<(string Kind, NotifyDecision Decision)> Notifications = [];
        public List<MaintenanceWindow> Windows { get; } = [];

        public Task<IReadOnlyList<Incident>> ActiveAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Incident>>(Incidents.Values.Where(i => i.IsActive).ToList());
        public Task<Incident?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Incidents.GetValueOrDefault(id));
        public Task<IReadOnlyList<IncidentSummary>> ListAsync(IncidentStatus? status, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<TimelineEntry>> TimelineAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<MaintenanceWindow>> MaintenanceWindowsAsync(DateTime at, CancellationToken ct) => Task.FromResult<IReadOnlyList<MaintenanceWindow>>(Windows);
        public Task<int> NotificationsSinceAsync(DateTime since, CancellationToken ct) => Task.FromResult(Notifications.Count(n => n.Decision == NotifyDecision.Send));

        public Task SaveAsync(Incident incident, CancellationToken ct)
        {
            Incidents[incident.Id] = incident;
            return Task.CompletedTask;
        }

        public Task RecordNotificationAsync(Guid incidentId, string kind, NotifyDecision decision, DateTime at, CancellationToken ct)
        {
            Notifications.Add((kind, decision));
            return Task.CompletedTask;
        }
    }

    sealed class FakeNotifier : INotifier
    {
        public readonly List<Notification> Sent = [];

        public Task NotifyAsync(Notification notification, CancellationToken ct)
        {
            Sent.Add(notification);
            return Task.CompletedTask;
        }
    }

    readonly FakeClock _clock = new();
    readonly FakeMetrics _metrics = new();
    readonly FakeCluster _cluster = new();
    readonly FakeStore _store = new();
    readonly FakeNotifier _notifier = new();

    DetectIncidents Detector() => new(_metrics, _cluster, _store, _store, _notifier, _clock, new MonitoringOptions
    {
        ConsoleUrl = "http://console.test",
        Services =
        [
            new MonitoredService
            {
                Name = "shop",
                Namespace = "shop",
                Slos = [new SloDefinition { Name = "availability", Objective = 0.995, ErrorRatioQuery = "errors[{window}]" }],
            },
        ],
    });

    [Fact]
    public async Task A_burning_slo_opens_one_incident_and_notifies_once()
    {
        _metrics.Ratio = 0.2;
        var first = await Detector().RunAsync(CancellationToken.None);
        _clock.UtcNow = _clock.UtcNow.AddSeconds(30);
        var second = await Detector().RunAsync(CancellationToken.None);

        Assert.Equal(1, first.Opened);
        Assert.Equal(0, second.Opened);
        var incident = Assert.Single(_store.Incidents.Values);
        Assert.Equal(Severity.Critical, incident.Severity);
        Assert.Contains(incident.Evidence, e => e.Query == "errors[1h]");
        var sent = Assert.Single(_notifier.Sent);
        Assert.StartsWith("[Critical]", sent.Subject);
        Assert.Contains($"http://console.test/#/incidents/{incident.Id}", sent.Body);
    }

    [Fact]
    public async Task Recovery_resolves_after_the_recovery_period_and_says_so()
    {
        _metrics.Ratio = 0.2;
        await Detector().RunAsync(CancellationToken.None);
        _metrics.Ratio = 0;
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await Detector().RunAsync(CancellationToken.None);
        Assert.True(_store.Incidents.Values.Single().IsActive);
        _clock.UtcNow = _clock.UtcNow.AddMinutes(11);
        var result = await Detector().RunAsync(CancellationToken.None);

        Assert.Equal(1, result.Resolved);
        Assert.False(_store.Incidents.Values.Single(i => i.Fingerprint.Kind.StartsWith("slo_burn")).IsActive);
        Assert.StartsWith("[Resolved]", _notifier.Sent.Last().Subject);
    }

    [Fact]
    public async Task Losing_the_metrics_keeps_open_incidents_open_and_raises_its_own_incident()
    {
        _metrics.Ratio = 0.2;
        await Detector().RunAsync(CancellationToken.None);
        _metrics.Down = true;
        for (var i = 0; i < 3; i++)
        {
            _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
            await Detector().RunAsync(CancellationToken.None);
        }

        var active = _store.Incidents.Values.Where(i => i.IsActive).Select(i => i.Fingerprint.Kind).Order().ToList();
        Assert.Equal([DetectIncidents.SignalUnavailable, "slo_burn:availability"], active);
    }

    [Fact]
    public async Task Maintenance_windows_record_but_do_not_send()
    {
        _store.Windows.Add(new MaintenanceWindow("shop", _clock.UtcNow.AddMinutes(-1), _clock.UtcNow.AddHours(1), "planned deploy"));
        _metrics.Ratio = 0.2;
        await Detector().RunAsync(CancellationToken.None);

        Assert.Empty(_notifier.Sent);
        Assert.Contains(_store.Notifications, n => n.Decision == NotifyDecision.Maintenance);
    }

    [Fact]
    public async Task Orders_stopping_at_a_busy_hour_opens_an_anomaly_incident()
    {
        var now = _clock.UtcNow;
        _metrics.Series = (_, at) => at is null ? 2 : 40 + (now - at.Value).Days;
        var detector = new DetectIncidents(_metrics, _cluster, _store, _store, _notifier, _clock, new MonitoringOptions
        {
            Services = [new MonitoredService { Name = "shop", Anomalies = [new AnomalyDefinition { Name = "orders", Query = "orders_per_minute", Direction = "down" }] }],
        });
        await detector.RunAsync(CancellationToken.None);

        var incident = Assert.Single(_store.Incidents.Values);
        Assert.Equal("anomaly:orders", incident.Fingerprint.Kind);
        Assert.Contains("below its usual level", incident.Title);
        Assert.Contains(incident.Evidence, e => e.Value == "44");
    }

    [Fact]
    public async Task Cluster_conditions_open_incidents_next_to_slo_ones()
    {
        _cluster.Snapshot = new WorkloadSnapshot([new AutoscalerState("api", 8, 4, 8, 8)], []);
        var result = await Detector().RunAsync(CancellationToken.None);

        Assert.Equal(1, result.Opened);
        Assert.Equal("autoscaler_at_max", _store.Incidents.Values.Single().Fingerprint.Kind);
    }
}
