using ReapOps.Domain.Incidents;

namespace ReapOps.Domain.Slos;

public sealed record Slo(string Name, double Objective)
{
    public double ErrorBudget => 1 - Objective;

    public static Slo Create(string name, double objective)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("An SLO needs a name", nameof(name));
        if (objective is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(objective), "The objective must be between 0 and 1, for example 0.995");
        return new Slo(name.Trim(), objective);
    }

    public double BurnRate(double errorRatio) => errorRatio <= 0 ? 0 : errorRatio / ErrorBudget;
}

public sealed record BurnWindow(TimeSpan Long, TimeSpan Short, double Threshold, Severity Severity);

public sealed record WindowReading(TimeSpan Window, double ErrorRatio);

public sealed record BurnAlert(Severity Severity, BurnWindow Rule, double LongBurn, double ShortBurn);

public static class BurnRatePolicy
{
    public static readonly IReadOnlyList<BurnWindow> Default =
    [
        new(TimeSpan.FromHours(1), TimeSpan.FromMinutes(5), 14.4, Severity.Critical),
        new(TimeSpan.FromHours(6), TimeSpan.FromMinutes(30), 6, Severity.Critical),
        new(TimeSpan.FromDays(1), TimeSpan.FromHours(2), 3, Severity.Warning),
    ];

    public static IEnumerable<TimeSpan> Windows(IEnumerable<BurnWindow> rules) =>
        rules.SelectMany(r => new[] { r.Long, r.Short }).Distinct().Order();

    public static BurnAlert? Evaluate(Slo slo, IReadOnlyCollection<WindowReading> readings, IReadOnlyList<BurnWindow>? rules = null)
    {
        var byWindow = readings.ToDictionary(r => r.Window, r => r.ErrorRatio);
        BurnAlert? worst = null;
        foreach (var rule in rules ?? Default)
        {
            if (!byWindow.TryGetValue(rule.Long, out var longRatio) || !byWindow.TryGetValue(rule.Short, out var shortRatio)) continue;
            var longBurn = slo.BurnRate(longRatio);
            var shortBurn = slo.BurnRate(shortRatio);
            if (longBurn < rule.Threshold || shortBurn < rule.Threshold) continue;
            if (worst is null || rule.Severity > worst.Severity) worst = new BurnAlert(rule.Severity, rule, longBurn, shortBurn);
        }
        return worst;
    }
}
