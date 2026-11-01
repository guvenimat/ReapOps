using Npgsql;
using ReapOps.Application.Detection;

namespace ReapOps.Worker;

public sealed class DetectionLoop(DetectIncidents detect, MonitoringOptions options, ILogger<DetectionLoop> log) : BackgroundService
{
    public static readonly string HeartbeatFile = Path.Combine(Path.GetTempPath(), "reapops-worker.alive");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("Watching {Count} services every {Seconds} s", options.Services.Count, options.IntervalSeconds);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await detect.RunAsync(ct);
                if (r.Opened + r.Resolved + r.Notified > 0 || r.Unavailable.Count > 0)
                    log.LogInformation("Detection: {Conditions} conditions, {Opened} opened, {Resolved} resolved, {Notified} notified, unavailable: {Unavailable}",
                        r.Conditions, r.Opened, r.Resolved, r.Notified, string.Join(", ", r.Unavailable));
                await File.WriteAllTextAsync(HeartbeatFile, DateTime.UtcNow.ToString("O"), ct);
            }
            catch (Exception ex) when (ex is NpgsqlException or IOException or System.Net.Mail.SmtpException)
            {
                log.LogWarning("Detection run failed: {Message}", ex.Message);
            }
            await Task.Delay(TimeSpan.FromSeconds(options.IntervalSeconds), ct);
        }
    }
}
