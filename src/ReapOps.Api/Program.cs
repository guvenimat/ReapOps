using System.Text.Json;
using ReapOps.Api;
using ReapOps.Application.Incidents;
using ReapOps.Infrastructure;

if (args is ["health", var url])
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var res = await http.GetAsync(url);
        Environment.ExitCode = res.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        Environment.ExitCode = 1;
    }
    return;
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default);
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});
builder.Services.AddReapOpsInfrastructure(builder.Configuration);
builder.Services.AddSingleton<OperatorSessions>();
builder.Services.AddSingleton<IncidentQueries>();
builder.Services.AddSingleton<AcknowledgeIncident>();
builder.Services.AddSingleton<ResolveIncident>();

var app = builder.Build();
app.UseMiddleware<OperatorAuthMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapConsoleApi();
app.MapFallbackToFile("index.html");
app.Run();
