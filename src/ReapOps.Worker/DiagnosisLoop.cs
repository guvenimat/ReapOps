using Npgsql;
using ReapOps.Application.Diagnosis;
using ReapOps.Application.Ports;
using ReapOps.Domain.Agent;

namespace ReapOps.Worker;

public sealed class DiagnosisLoop(DiagnoseIncident diagnose, IDiagnosisModel model, IDiagnosisReader pending, IIncidentReader incidents, AgentOptions options,
    ILogger<DiagnosisLoop> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (options.ParsedMode == AgentMode.Off || !model.IsConfigured)
        {
            log.LogInformation("Diagnosis agent is off (mode {Mode}, model configured: {Configured})", options.ParsedMode, model.IsConfigured);
            return;
        }
        log.LogInformation("Diagnosis agent running in {Mode} mode with {Model}", options.ParsedMode, model.Name);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var id in await pending.PendingAsync(3, ct))
                {
                    if (await incidents.GetAsync(id, ct) is not { } incident) continue;
                    var d = await diagnose.RunAsync(incident, ct);
                    log.LogInformation("Diagnosed incident {Incident}: {Status}, confidence {Confidence}, {Calls} tool calls, {In}+{Out} tokens",
                        id, d.Status, d.Confidence, d.ToolCalls, d.InputTokens, d.OutputTokens);
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or HttpRequestException or InvalidOperationException)
            {
                log.LogWarning("Diagnosis run failed: {Message}", ex.Message);
            }
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }
}
