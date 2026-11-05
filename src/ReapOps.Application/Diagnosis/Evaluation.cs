using ReapOps.Application.Ports;
using ReapOps.Domain.Agent;
using ReapOps.Domain.Incidents;

namespace ReapOps.Application.Diagnosis;

public sealed record RecordedOutput(string Tool, string Match, string Output);

public sealed record EvalExpectation(IReadOnlyList<string> FirstActions, IReadOnlyList<string> ForbiddenActions, IReadOnlyList<IReadOnlyList<string>> RootCauseTerms);

public sealed record EvalCase(string Name, Condition Incident, IReadOnlyList<RecordedOutput> Recorded, EvalExpectation Expect);

public sealed record EvalScore(string Case, bool FirstAction, bool AvoidedForbidden, bool RootCause, bool Grounded, DiagnosisStatus Status, int ToolCalls,
    int InputTokens, int OutputTokens, string Detail, Domain.Agent.Diagnosis? Diagnosis = null, IReadOnlyList<string>? Trace = null)
{
    public bool Passed => FirstAction && AvoidedForbidden && RootCause && Grounded;
}

public sealed record EvalReport(string Model, string PromptVersion, IReadOnlyList<EvalScore> Scores)
{
    public int Passed => Scores.Count(s => s.Passed);
}

public sealed class ReplayTool(ToolSpec spec, IReadOnlyList<RecordedOutput> recorded, List<string>? trace = null) : IAgentTool
{
    public ToolSpec Spec => spec;

    public Task<string> ExecuteAsync(IReadOnlyDictionary<string, string> arguments, CancellationToken ct)
    {
        var args = string.Join(" ", arguments.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => a.Value));
        var output = spec.Name == ListMetricsTool.Definition.Name ? ListMetrics(arguments) : Lookup(args);
        trace?.Add($"{spec.Name}({args}) -> {output.Replace('\n', ' ')}");
        return Task.FromResult(output);
    }

    string Lookup(string args)
    {
        var hit = recorded.FirstOrDefault(r => r.Tool == spec.Name && args.Contains(r.Match, StringComparison.OrdinalIgnoreCase))
            ?? recorded.FirstOrDefault(r => r.Tool == spec.Name && r.Match == "*");
        return hit?.Output ?? "no data";
    }

    string ListMetrics(IReadOnlyDictionary<string, string> arguments)
    {
        var names = recorded.Where(r => r.Tool == spec.Name).SelectMany(r => r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (arguments.TryGetValue("contains", out var word) && !string.IsNullOrWhiteSpace(word))
            names = names.Where(n => n.Contains(word.Trim(), StringComparison.OrdinalIgnoreCase));
        var shown = names.ToList();
        return shown.Count == 0 ? "no metrics" : string.Join("\n", shown);
    }
}

public sealed class EvaluateDiagnoses(IDiagnosisModel model, IClock clock, AgentOptions options)
{
    sealed class DiscardStore : IDiagnosisStore
    {
        public Task SaveAsync(Domain.Agent.Diagnosis diagnosis, CancellationToken ct) => Task.CompletedTask;
    }

    public static readonly IReadOnlyList<ToolSpec> Tools =
    [
        QueryMetricsTool.Definition,
        ListMetricsTool.Definition,
        WorkloadStatusTool.Definition,
        IncidentHistoryTool.Definition,
    ];

    public async Task<EvalReport> RunAsync(IReadOnlyList<EvalCase> cases, CancellationToken ct)
    {
        var scores = new List<EvalScore>();
        foreach (var c in cases)
        {
            var trace = new List<string>();
            var tools = Tools.Select(spec => (IAgentTool)new ReplayTool(spec, c.Recorded, trace)).ToList();
            var diagnose = new DiagnoseIncident(model, tools, new DiscardStore(), clock, options);
            var incident = Incident.Open(Guid.CreateVersion7(clock.UtcNow), c.Incident, clock.UtcNow);
            try
            {
                var d = await diagnose.RunAsync(incident, ct);
                scores.Add(Score(c, d) with { Diagnosis = d, Trace = trace });
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                scores.Add(new EvalScore(c.Name, false, false, false, false, DiagnosisStatus.Failed, 0, 0, 0, $"run failed: {ex.Message.Split('\n')[0]}"));
            }
        }
        return new EvalReport(model.Name, DiagnoseIncident.PromptVersion, scores);
    }

    public static EvalScore Score(EvalCase c, Domain.Agent.Diagnosis d)
    {
        var first = d.Options.FirstOrDefault()?.ActionId ?? "";
        var firstOk = c.Expect.FirstActions.Count == 0 || c.Expect.FirstActions.Contains(first);
        var forbiddenOk = !d.Options.Any(o => c.Expect.ForbiddenActions.Contains(o.ActionId));
        var text = $"{d.Summary} {d.RootCause}";
        var missing = c.Expect.RootCauseTerms.Where(any => !any.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
        var detail = $"first action '{first}'" + (missing.Count > 0 ? $"; root cause misses: {string.Join(" / ", missing.Select(m => string.Join("|", m)))}" : "");
        return new EvalScore(c.Name, firstOk, forbiddenOk, missing.Count == 0, d.Evidence.Count > 0, d.Status, d.ToolCalls, d.InputTokens, d.OutputTokens, detail);
    }
}
