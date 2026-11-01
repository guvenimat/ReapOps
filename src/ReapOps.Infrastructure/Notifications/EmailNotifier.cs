using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ReapOps.Application.Ports;

namespace ReapOps.Infrastructure.Notifications;

public sealed class EmailNotifier(IConfiguration cfg, ILogger<EmailNotifier> log) : INotifier
{
    readonly string? _host = cfg["Smtp:Host"];
    readonly int _port = int.TryParse(cfg["Smtp:Port"], out var p) ? p : 25;
    readonly MailAddress _from = new(cfg["Smtp:From"] ?? "reapops@reapops.local", "ReapOps");
    readonly string[] _to = (cfg["Notifications:To"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public async Task NotifyAsync(Notification n, CancellationToken ct)
    {
        if (_host is null || _to.Length == 0)
        {
            log.LogInformation("Notification (no email configured): {Subject}", n.Subject);
            return;
        }
        using var message = new MailMessage { From = _from, Subject = n.Subject, Body = n.Body, IsBodyHtml = false };
        foreach (var to in _to) message.To.Add(to);
        using var client = new SmtpClient(_host, _port);
        await client.SendMailAsync(message, ct);
        log.LogInformation("Sent {Kind} notification for incident {Incident}: {Subject}", n.Kind, n.IncidentId, n.Subject);
    }
}
