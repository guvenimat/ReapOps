using Npgsql;
using ReapOps.Application.Detection;
using ReapOps.Infrastructure;
using ReapOps.Infrastructure.Persistence;
using ReapOps.Worker;

if (args is ["health", "heartbeat", var maxAge])
{
    var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(DetectionLoop.HeartbeatFile);
    Environment.ExitCode = File.Exists(DetectionLoop.HeartbeatFile) && age < TimeSpan.FromSeconds(int.Parse(maxAge)) ? 0 : 1;
    return;
}

var builder = Host.CreateApplicationBuilder(args);
var monitoring = new MonitoringOptions();
builder.Configuration.GetSection("Monitoring").Bind(monitoring);
builder.Services.AddSingleton(monitoring);
builder.Services.AddReapOpsInfrastructure(builder.Configuration).AddReapOpsSignals();
builder.Services.AddSingleton<DetectIncidents>();
builder.Services.AddHostedService<DetectionLoop>();

var app = builder.Build();
if (args.Contains("migrate"))
{
    await Database.MigrateAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), app.Services.GetRequiredService<ILogger<Program>>(), CancellationToken.None);
    return;
}
app.Run();
