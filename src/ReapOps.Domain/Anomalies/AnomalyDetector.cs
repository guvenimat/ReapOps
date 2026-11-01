namespace ReapOps.Domain.Anomalies;

public enum Direction { Up, Down, Both }

public sealed record AnomalyRule(string Name, Direction Direction, double Threshold = 6, double MinRelativeChange = 0.5, int MinHistory = 5)
{
    public static AnomalyRule Create(string name, Direction direction, double? threshold = null, double? minRelativeChange = null, int? minHistory = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("An anomaly rule needs a name", nameof(name));
        var rule = new AnomalyRule(name.Trim(), direction, threshold ?? 6, minRelativeChange ?? 0.5, minHistory ?? 5);
        if (rule.Threshold <= 0) throw new ArgumentOutOfRangeException(nameof(threshold), "The threshold must be positive");
        if (rule.MinRelativeChange < 0) throw new ArgumentOutOfRangeException(nameof(minRelativeChange), "The minimum change cannot be negative");
        if (rule.MinHistory < 3) throw new ArgumentOutOfRangeException(nameof(minHistory), "At least three past values are needed");
        return rule;
    }
}

public sealed record AnomalyScore(double Current, double Median, double Spread, double Score, double RelativeChange, int History);

public enum AnomalyVerdict { Normal, Anomalous, NotEnoughHistory }

public static class AnomalyDetector
{
    const double MadToSigma = 1.4826;

    public static AnomalyScore Score(double current, IReadOnlyCollection<double> history)
    {
        var values = history.Where(double.IsFinite).Order().ToArray();
        if (values.Length == 0) return new AnomalyScore(current, double.NaN, double.NaN, 0, 0, 0);
        var median = Median(values);
        var mad = Median(values.Select(v => Math.Abs(v - median)).Order().ToArray());
        var spread = Math.Max(MadToSigma * mad, Math.Max(Math.Abs(median) * 0.05, 1e-9));
        var relative = median == 0 ? (current == 0 ? 0 : double.PositiveInfinity) : (current - median) / Math.Abs(median);
        return new AnomalyScore(current, median, spread, (current - median) / spread, relative, values.Length);
    }

    public static AnomalyVerdict Judge(AnomalyRule rule, AnomalyScore score)
    {
        if (score.History < rule.MinHistory) return AnomalyVerdict.NotEnoughHistory;
        var up = score.Score >= rule.Threshold && score.RelativeChange >= rule.MinRelativeChange;
        var down = score.Score <= -rule.Threshold && score.RelativeChange <= -rule.MinRelativeChange;
        return rule.Direction switch
        {
            Direction.Up => up,
            Direction.Down => down,
            _ => up || down,
        } ? AnomalyVerdict.Anomalous : AnomalyVerdict.Normal;
    }

    static double Median(double[] sorted) =>
        sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
}
