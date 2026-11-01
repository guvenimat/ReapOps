using System.Globalization;

namespace ReapOps.Domain.Incidents;

public sealed record AutoscalerState(string Name, int Current, int Min, int Max, int Desired);

public sealed record PodState(string Name, string Workload, string? WaitingReason, string? LastTerminationReason, int Restarts);

public sealed record WorkloadSnapshot(IReadOnlyList<AutoscalerState> Autoscalers, IReadOnlyList<PodState> Pods);

public static class WorkloadRules
{
    public const int RestartThreshold = 3;

    public static IEnumerable<Condition> Evaluate(string service, WorkloadSnapshot snapshot)
    {
        foreach (var hpa in snapshot.Autoscalers.Where(h => h.Max > 0 && h.Current >= h.Max && h.Desired >= h.Max))
            yield return new Condition(new Fingerprint(service, "autoscaler_at_max", hpa.Name), Severity.Warning,
                $"{hpa.Name} is running at its autoscaling ceiling ({hpa.Current}/{hpa.Max} replicas)",
                [
                    new Evidence("Replicas", $"{hpa.Current} of max {hpa.Max} (min {hpa.Min})"),
                    new Evidence("Desired replicas", hpa.Desired.ToString(CultureInfo.InvariantCulture)),
                ]);

        foreach (var group in snapshot.Pods.GroupBy(p => p.Workload))
        {
            var crashing = group.Where(p => p.WaitingReason == "CrashLoopBackOff").ToList();
            if (crashing.Count > 0)
                yield return new Condition(new Fingerprint(service, "crash_loop", group.Key), Severity.Critical,
                    $"{group.Key} is crash looping ({crashing.Count} of {group.Count()} pods)",
                    crashing.Select(p => new Evidence(p.Name, $"CrashLoopBackOff, {p.Restarts} restarts, last exit: {p.LastTerminationReason ?? "unknown"}")).ToList());

            var oom = group.Where(p => p.LastTerminationReason == "OOMKilled" && p.Restarts > 0).ToList();
            if (oom.Count > 0)
                yield return new Condition(new Fingerprint(service, "oom_killed", group.Key), Severity.Warning,
                    $"{group.Key} was killed for running out of memory ({oom.Count} pods)",
                    oom.Select(p => new Evidence(p.Name, $"OOMKilled, {p.Restarts} restarts")).ToList());

            var unstable = group.Where(p => p.Restarts >= RestartThreshold && p.WaitingReason != "CrashLoopBackOff" && p.LastTerminationReason != "OOMKilled").ToList();
            if (unstable.Count > 0)
                yield return new Condition(new Fingerprint(service, "restarts", group.Key), Severity.Warning,
                    $"{group.Key} keeps restarting",
                    unstable.Select(p => new Evidence(p.Name, $"{p.Restarts} restarts, last exit: {p.LastTerminationReason ?? "unknown"}")).ToList());
        }
    }
}
