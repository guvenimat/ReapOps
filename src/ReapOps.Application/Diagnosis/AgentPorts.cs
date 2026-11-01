using ReapOps.Domain.Agent;

namespace ReapOps.Application.Diagnosis;

public sealed record ToolParameter(string Name, string Description, bool Required = true);

public sealed record ToolSpec(string Name, string Description, IReadOnlyList<ToolParameter> Parameters);

public sealed record ToolCall(string Id, string Name, IReadOnlyDictionary<string, string> Arguments, string? Opaque = null);

public sealed record ToolOutcome(string CallId, string Content, bool IsError);

public sealed record AgentTurn(string Role, string? Text, IReadOnlyList<ToolCall> Calls, IReadOnlyList<ToolOutcome> Results)
{
    public static AgentTurn Brief(string text) => new("user", text, [], []);
    public static AgentTurn Assistant(IReadOnlyList<ToolCall> calls) => new("assistant", null, calls, []);
    public static AgentTurn ToolResults(IReadOnlyList<ToolOutcome> results) => new("user", null, [], results);
}

public sealed record ModelRequest(string System, IReadOnlyList<AgentTurn> Transcript, IReadOnlyList<ToolSpec> Tools, IReadOnlyList<CatalogAction> Actions);

public sealed record ModelReply(IReadOnlyList<ToolCall> Calls, DiagnosisDraft? Final, string? Refusal, int InputTokens, int OutputTokens);

public interface IDiagnosisModel
{
    string Name { get; }
    bool IsConfigured { get; }
    Task<ModelReply> NextAsync(ModelRequest request, CancellationToken ct);
}

public interface IDiagnosisStore
{
    Task SaveAsync(Domain.Agent.Diagnosis diagnosis, CancellationToken ct);
}

public interface IDiagnosisReader
{
    Task<IReadOnlyList<Domain.Agent.Diagnosis>> ForIncidentAsync(Guid incidentId, CancellationToken ct);
    Task<IReadOnlyList<Guid>> PendingAsync(int limit, CancellationToken ct);
}

public interface IAgentTool
{
    ToolSpec Spec { get; }
    Task<string> ExecuteAsync(IReadOnlyDictionary<string, string> arguments, CancellationToken ct);
}

public sealed class AgentOptions
{
    public string Mode { get; set; } = "shadow";
    public int MaxSteps { get; set; } = 10;
    public int MaxTokens { get; set; } = 80_000;

    public AgentMode ParsedMode => Mode.ToLowerInvariant() switch
    {
        "off" => AgentMode.Off,
        "visible" => AgentMode.Visible,
        _ => AgentMode.Shadow,
    };
}
