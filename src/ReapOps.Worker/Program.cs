using Npgsql;
using ReapOps.Application.Detection;
using ReapOps.Application.Diagnosis;
using ReapOps.Infrastructure.Agent;
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
var agent = new AgentOptions();
builder.Configuration.GetSection("Agent").Bind(agent);
builder.Services.AddSingleton(agent);
builder.Services.AddReapOpsInfrastructure(builder.Configuration).AddReapOpsSignals().AddReapOpsAgent();
builder.Services.AddSingleton<DetectIncidents>();
builder.Services.AddHostedService<DetectionLoop>();
builder.Services.AddHostedService<DiagnosisLoop>();

var app = builder.Build();
if (args.Contains("migrate"))
{
    await Database.MigrateAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), app.Services.GetRequiredService<ILogger<Program>>(), CancellationToken.None);
    return;
}
if (args is ["eval", var directory, ..])
{
    var model = app.Services.GetRequiredService<IDiagnosisModel>();
    if (!model.IsConfigured)
    {
        Console.Error.WriteLine("No diagnosis model is configured; set Anthropic:ApiKey.");
        Environment.ExitCode = 2;
        return;
    }
    var report = await app.Services.GetRequiredService<EvaluateDiagnoses>().RunAsync(EvalCases.Load(directory), CancellationToken.None);
    var verbose = args.Contains("--verbose");
    foreach (var s in report.Scores)
    {
        Console.WriteLine($"{(s.Passed ? "PASS" : "FAIL")}  {s.Case}: action {s.FirstAction}, no forbidden {s.AvoidedForbidden}, root cause {s.RootCause}, grounded {s.Grounded}, {s.Status}, {s.ToolCalls} calls, {s.InputTokens}+{s.OutputTokens} tokens; {s.Detail}");
        if (!verbose) continue;
        foreach (var t in s.Trace ?? []) Console.WriteLine($"    tool  {t}");
        if (s.Diagnosis is not { } d) continue;
        Console.WriteLine($"    summary     {d.Summary}");
        Console.WriteLine($"    root cause  {d.RootCause} (confidence {d.Confidence})");
        foreach (var o in d.Options) Console.WriteLine($"    option      {o.ActionId} {string.Join(", ", o.Parameters.Select(p => $"{p.Key}={p.Value}"))}: {o.Rationale}");
        foreach (var a in d.Alternatives) Console.WriteLine($"    ruled out   {a.Hypothesis}: {a.Why}");
        foreach (var n in d.Notes) Console.WriteLine($"    note        {n}");
    }
    Console.WriteLine($"{report.Passed}/{report.Scores.Count} passed ({report.Model}, prompt {report.PromptVersion})");
    Environment.ExitCode = report.Passed == report.Scores.Count ? 0 : 1;
    return;
}
app.Run();
