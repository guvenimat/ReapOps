using System.Text.Json;
using System.Text.Json.Serialization;
using ReapOps.Application.Diagnosis;
using ReapOps.Domain.Incidents;

namespace ReapOps.Infrastructure.Agent;

public sealed record EvalCaseFile(string Name, string Service, string Kind, string Severity, string Title, List<EvidenceFile> Evidence,
    List<RecordedFile> Recorded, ExpectFile Expect);
public sealed record EvidenceFile(string Label, string Value, string? Query = null);
public sealed record RecordedFile(string Tool, string Match, string Output);
public sealed record ExpectFile(List<string> FirstActions, List<string> ForbiddenActions, List<List<string>> RootCauseTerms);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(EvalCaseFile))]
internal sealed partial class EvalJsonContext : JsonSerializerContext;

public static class EvalCases
{
    public static IReadOnlyList<EvalCase> Load(string directory) =>
        Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal).Select(f => Parse(File.ReadAllText(f))).ToList();

    public static EvalCase Parse(string json)
    {
        var f = JsonSerializer.Deserialize(json, EvalJsonContext.Default.EvalCaseFile) ?? throw new InvalidDataException("Empty eval case");
        var severity = f.Severity.ToLowerInvariant() switch { "critical" => Severity.Critical, "info" => Severity.Info, _ => Severity.Warning };
        var condition = new Condition(new Fingerprint(f.Service, f.Kind, f.Name), severity, f.Title,
            f.Evidence.Select(e => new Evidence(e.Label, e.Value, e.Query)).ToList());
        return new EvalCase(f.Name, condition, f.Recorded.Select(r => new RecordedOutput(r.Tool, r.Match, r.Output)).ToList(),
            new EvalExpectation(f.Expect.FirstActions, f.Expect.ForbiddenActions, f.Expect.RootCauseTerms.Select(t => (IReadOnlyList<string>)t).ToList()));
    }
}

public sealed class UnconfiguredModel : IDiagnosisModel
{
    public string Name => "none";
    public bool IsConfigured => false;

    public Task<ModelReply> NextAsync(ModelRequest request, CancellationToken ct) =>
        throw new InvalidOperationException("No diagnosis model is configured. Set Anthropic:ApiKey to enable the agent.");
}
