using System.Diagnostics;
using ReapOps.Application.Detection;

namespace ReapOps.Worker;

public sealed class DetectionLoop(DetectIncidents detect, MonitoringOptions options, ILogger<DetectionLoop> log) : BackgroundService
{
    public static readonly string HeartbeatFile = Path.Combine(Path.GetTempPath(), "reapops-worker.alive");
    static readonly TimeSpan SummaryEvery = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("Watching {Count} services every {Seconds} s", options.Services.Count, options.IntervalSeconds);
        var since = Stopwatch.StartNew();
        int runs = 0, failures = 0, unavailable = 0;
        long lastMs = 0;
        while (!ct.IsCancellationRequested)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                var r = await detect.RunAsync(ct);
                runs++;
                if (r.Unavailable.Count > 0) unavailable++;
                if (r.Opened + r.Resolved + r.Notified > 0 || r.Unavailable.Count > 0)
                    log.LogInformation("Detection: {Conditions} conditions, {Opened} opened, {Resolved} resolved, {Notified} notified, unavailable: {Unavailable}",
                        r.Conditions, r.Opened, r.Resolved, r.Notified, string.Join(", ", r.Unavailable));
                await File.WriteAllTextAsync(HeartbeatFile, DateTime.UtcNow.ToString("O"), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failures++;
                log.LogError(ex, "Detection run failed with {Type}: {Message}", ex.GetType().Name, ex.Message);
            }
            lastMs = timer.ElapsedMilliseconds;
            if (since.Elapsed >= SummaryEvery)
            {
                log.LogInformation("Detection summary: {Runs} runs, {Failures} failed, {Unavailable} with an unreadable signal, last run {Ms} ms",
                    runs, failures, unavailable, lastMs);
                runs = failures = unavailable = 0;
                since.Restart();
            }
            await Task.Delay(TimeSpan.FromSeconds(options.IntervalSeconds), ct);
        }
    }
}
