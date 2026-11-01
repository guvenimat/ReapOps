using System.Text.Json.Serialization;
using Npgsql;
using ReapOps.Application.Incidents;
using ReapOps.Application.Ports;
using ReapOps.Domain.Common;
using ReapOps.Domain.Incidents;

namespace ReapOps.Api;

public sealed record LoginRequest(string? Name, string? Password);
public sealed record OperatorView(string Name);
public sealed record ErrorView(string Error);
public sealed record IncidentListView(IReadOnlyList<IncidentSummary> Incidents);

public sealed class SeverityConverter() : JsonStringEnumConverter<Severity>(System.Text.Json.JsonNamingPolicy.SnakeCaseLower);
public sealed class StatusConverter() : JsonStringEnumConverter<IncidentStatus>(System.Text.Json.JsonNamingPolicy.SnakeCaseLower);
public sealed class ConfidenceConverter() : JsonStringEnumConverter<Domain.Agent.Confidence>(System.Text.Json.JsonNamingPolicy.SnakeCaseLower);
public sealed class DiagnosisStatusConverter() : JsonStringEnumConverter<Domain.Agent.DiagnosisStatus>(System.Text.Json.JsonNamingPolicy.SnakeCaseLower);
public sealed class AgentModeConverter() : JsonStringEnumConverter<Domain.Agent.AgentMode>(System.Text.Json.JsonNamingPolicy.SnakeCaseLower);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, Converters = [typeof(SeverityConverter), typeof(StatusConverter), typeof(ConfidenceConverter), typeof(DiagnosisStatusConverter), typeof(AgentModeConverter)])]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(OperatorView))]
[JsonSerializable(typeof(ErrorView))]
[JsonSerializable(typeof(IncidentListView))]
[JsonSerializable(typeof(IncidentDetail))]
public sealed partial class ApiJsonContext : JsonSerializerContext;

public static class ConsoleApi
{
    public static void MapConsoleApi(this WebApplication app)
    {
        app.MapGet("/livez", () => Results.Text("ok"));
        app.MapGet("/healthz", async (NpgsqlDataSource db, CancellationToken ct) =>
        {
            try
            {
                await using var conn = await db.OpenConnectionAsync(ct);
                return Results.Text("ok");
            }
            catch (NpgsqlException)
            {
                return Results.Text("database unreachable", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        var api = app.MapGroup("/api");
        api.MapPost("/auth/login", (LoginRequest req, OperatorSessions sessions, HttpContext ctx) =>
        {
            var name = req.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 60 || req.Password is null || !sessions.CheckPassword(req.Password))
                return Results.Json(new ErrorView("Wrong name or password"), ApiJsonContext.Default.ErrorView, statusCode: StatusCodes.Status401Unauthorized);
            ctx.Response.Cookies.Append(OperatorSessions.Cookie, sessions.Issue(name, DateTime.UtcNow), new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = ctx.Request.IsHttps,
                MaxAge = OperatorSessions.Lifetime,
            });
            return Results.Json(new OperatorView(name), ApiJsonContext.Default.OperatorView);
        });
        api.MapPost("/auth/logout", (HttpContext ctx) =>
        {
            ctx.Response.Cookies.Delete(OperatorSessions.Cookie);
            return Results.NoContent();
        });
        api.MapGet("/me", (HttpContext ctx) => Results.Json(new OperatorView(Op(ctx).Name), ApiJsonContext.Default.OperatorView));

        api.MapGet("/incidents", async (string? status, IncidentQueries queries, CancellationToken ct) =>
        {
            IncidentStatus? s = status switch
            {
                "open" => IncidentStatus.Open,
                "acknowledged" => IncidentStatus.Acknowledged,
                "resolved" => IncidentStatus.Resolved,
                _ => null,
            };
            if (s is null && !string.IsNullOrEmpty(status))
                return Results.Json(new ErrorView("status must be open, acknowledged or resolved"), ApiJsonContext.Default.ErrorView, statusCode: StatusCodes.Status400BadRequest);
            return Results.Json(new IncidentListView(await queries.ListAsync(s, ct)), ApiJsonContext.Default.IncidentListView);
        });
        api.MapGet("/incidents/{id:guid}", async (Guid id, IncidentQueries queries, CancellationToken ct) =>
            await queries.DetailAsync(id, ct) is { } detail
                ? Results.Json(detail, ApiJsonContext.Default.IncidentDetail)
                : Results.Json(new ErrorView("Incident not found"), ApiJsonContext.Default.ErrorView, statusCode: StatusCodes.Status404NotFound));
        api.MapPost("/incidents/{id:guid}/acknowledge", async (Guid id, HttpContext ctx, AcknowledgeIncident ack, CancellationToken ct) =>
            await Change(() => ack.RunAsync(id, Op(ctx).Name, ct)));
        api.MapPost("/incidents/{id:guid}/resolve", async (Guid id, HttpContext ctx, ResolveIncident resolve, CancellationToken ct) =>
            await Change(() => resolve.RunAsync(id, Op(ctx).Name, ct)));
    }

    static Operator Op(HttpContext ctx) => (Operator)ctx.Items[nameof(Operator)]!;

    static async Task<IResult> Change(Func<Task<IncidentChange>> action)
    {
        try
        {
            return await action() == IncidentChange.Changed
                ? Results.NoContent()
                : Results.Json(new ErrorView("Incident not found"), ApiJsonContext.Default.ErrorView, statusCode: StatusCodes.Status404NotFound);
        }
        catch (DomainException ex)
        {
            return Results.Json(new ErrorView(ex.Message), ApiJsonContext.Default.ErrorView, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
