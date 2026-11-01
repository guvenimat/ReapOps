namespace ReapOps.Domain.Agent;

public enum Confidence { Low, Medium, High }

public enum DiagnosisStatus { Completed, Escalated, Failed }

public enum AgentMode { Off, Shadow, Visible }

public sealed record Claim(string Statement, IReadOnlyList<string> Sources);

public sealed record RejectedHypothesis(string Hypothesis, string Why);

public sealed record ProposedOption(string ActionId, IReadOnlyDictionary<string, string> Parameters, string Rationale);

public sealed record DiagnosisDraft(string Summary, string RootCause, Confidence Confidence, IReadOnlyList<Claim> Evidence,
    IReadOnlyList<RejectedHypothesis> Alternatives, IReadOnlyList<ProposedOption> Options);

public sealed record Diagnosis(Guid Id, Guid IncidentId, DiagnosisStatus Status, AgentMode Mode, string Model, string PromptVersion, string Summary,
    string RootCause, Confidence Confidence, IReadOnlyList<Claim> Evidence, IReadOnlyList<RejectedHypothesis> Alternatives,
    IReadOnlyList<ProposedOption> Options, IReadOnlyList<string> Notes, int ToolCalls, int InputTokens, int OutputTokens, DateTime CreatedAt);

public static class DiagnosisPolicy
{
    public const int MaxOptions = 3;

    public static (DiagnosisDraft Draft, IReadOnlyList<string> Notes) Check(DiagnosisDraft draft, IReadOnlySet<string> executedCalls)
    {
        var notes = new List<string>();
        var options = new List<ProposedOption>();
        foreach (var option in draft.Options)
        {
            if (ActionCatalog.Find(option.ActionId) is not { } action)
            {
                notes.Add($"Dropped option '{option.ActionId}': not in the action catalog");
                continue;
            }
            var unknown = option.Parameters.Keys.Except(action.Parameters).ToList();
            if (unknown.Count > 0)
            {
                notes.Add($"Dropped option '{option.ActionId}': unknown parameters {string.Join(", ", unknown)}");
                continue;
            }
            if (options.Count == MaxOptions)
            {
                notes.Add($"Dropped option '{option.ActionId}': at most {MaxOptions} options");
                continue;
            }
            options.Add(option);
        }

        var evidence = new List<Claim>();
        foreach (var claim in draft.Evidence)
        {
            var cited = claim.Sources.Where(executedCalls.Contains).ToList();
            if (cited.Count == 0) notes.Add($"Dropped a claim without a source from this run: {claim.Statement}");
            else evidence.Add(claim with { Sources = cited });
        }

        var confidence = draft.Confidence;
        if (evidence.Count == 0 && confidence != Confidence.Low)
        {
            confidence = Confidence.Low;
            notes.Add("Confidence lowered: no claim is backed by a tool call");
        }
        return (draft with { Options = options, Evidence = evidence, Confidence = confidence }, notes);
    }
}
