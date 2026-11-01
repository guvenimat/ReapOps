using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Npgsql;
using ReapOps.Application.Diagnosis;
using ReapOps.Domain.Agent;

namespace ReapOps.Infrastructure.Agent;

public sealed record DiagnosisBody(IReadOnlyList<Claim> Evidence, IReadOnlyList<RejectedHypothesis> Alternatives, IReadOnlyList<ProposedOption> Options,
    IReadOnlyList<string> Notes);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(DiagnosisBody))]
internal sealed partial class AgentJsonContext : JsonSerializerContext;

public sealed class DiagnosisRepository(NpgsqlDataSource db) : IDiagnosisStore, IDiagnosisReader
{
    public sealed record DiagnosisRecord(Guid Id, Guid IncidentId, string Status, string Mode, string Model, string PromptVersion, string Summary,
        string RootCause, string Confidence, string Body, int ToolCalls, int InputTokens, int OutputTokens, DateTime CreatedAt);
    public sealed record IdRecord(Guid Id);
    public sealed record LimitRecord(int Limit);

    public async Task SaveAsync(Diagnosis d, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO diagnoses (id, incident_id, status, mode, model, prompt_version, summary, root_cause, confidence, body, tool_calls,
                input_tokens, output_tokens, created_at)
            VALUES (@Id, @IncidentId, @Status, @Mode, @Model, @PromptVersion, @Summary, @RootCause, @Confidence, @Body::jsonb, @ToolCalls,
                @InputTokens, @OutputTokens, @CreatedAt)
            """, new DiagnosisRecord(d.Id, d.IncidentId, Lower(d.Status), Lower(d.Mode), d.Model, d.PromptVersion, d.Summary, d.RootCause,
            Lower(d.Confidence), JsonSerializer.Serialize(new DiagnosisBody(d.Evidence, d.Alternatives, d.Options, d.Notes), AgentJsonContext.Default.DiagnosisBody),
            d.ToolCalls, d.InputTokens, d.OutputTokens, d.CreatedAt));
    }

    public async Task<IReadOnlyList<Diagnosis>> ForIncidentAsync(Guid incidentId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<DiagnosisRecord>("""
            SELECT id, incident_id, status, mode, model, prompt_version, summary, root_cause, confidence, body::text AS body, tool_calls,
                input_tokens, output_tokens, created_at
            FROM diagnoses WHERE incident_id = @Id ORDER BY created_at DESC
            """, new IdRecord(incidentId));
        return rows.Select(Restore).ToList();
    }

    public async Task<IReadOnlyList<Guid>> PendingAsync(int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<Guid>("""
            SELECT i.id FROM incidents i
            WHERE i.status <> 'resolved'
              AND NOT EXISTS (
                SELECT 1 FROM diagnoses d WHERE d.incident_id = i.id
                  AND d.created_at >= coalesce((SELECT max(e.at) FROM incident_events e WHERE e.incident_id = i.id AND e.kind IN ('opened', 'escalated')), i.opened_at))
            ORDER BY CASE i.severity WHEN 'critical' THEN 0 WHEN 'warning' THEN 1 ELSE 2 END, i.opened_at
            LIMIT @Limit
            """, new LimitRecord(limit))).AsList();
    }

    static Diagnosis Restore(DiagnosisRecord r)
    {
        var body = JsonSerializer.Deserialize(r.Body, AgentJsonContext.Default.DiagnosisBody) ?? new DiagnosisBody([], [], [], []);
        return new Diagnosis(r.Id, r.IncidentId, Enum.Parse<DiagnosisStatus>(r.Status, true), Enum.Parse<AgentMode>(r.Mode, true), r.Model, r.PromptVersion,
            r.Summary, r.RootCause, Enum.Parse<Confidence>(r.Confidence, true), body.Evidence, body.Alternatives, body.Options, body.Notes, r.ToolCalls,
            r.InputTokens, r.OutputTokens, r.CreatedAt);
    }

    static string Lower<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();
}
