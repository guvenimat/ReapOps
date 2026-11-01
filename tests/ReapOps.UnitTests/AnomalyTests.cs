using ReapOps.Domain.Anomalies;

namespace ReapOps.UnitTests;

public class AnomalyTests
{
    static readonly double[] Week = [100, 104, 96, 101, 99, 103, 98];

    [Fact]
    public void A_drop_far_below_the_usual_level_is_anomalous()
    {
        var score = AnomalyDetector.Score(20, Week);
        Assert.Equal(100, score.Median);
        Assert.True(score.Score < -6);
        Assert.Equal(AnomalyVerdict.Anomalous, AnomalyDetector.Judge(AnomalyRule.Create("orders", Direction.Down), score));
        Assert.Equal(AnomalyVerdict.Normal, AnomalyDetector.Judge(AnomalyRule.Create("orders", Direction.Up), score));
    }

    [Fact]
    public void Ordinary_variation_is_normal() =>
        Assert.Equal(AnomalyVerdict.Normal, AnomalyDetector.Judge(AnomalyRule.Create("rps", Direction.Both), AnomalyDetector.Score(108, Week)));

    [Fact]
    public void A_statistically_odd_but_small_change_is_not_reported()
    {
        double[] flat = [100, 100, 100, 100, 100, 100, 100];
        var score = AnomalyDetector.Score(140, flat);
        Assert.True(score.Score > 6);
        Assert.Equal(AnomalyVerdict.Normal, AnomalyDetector.Judge(AnomalyRule.Create("rps", Direction.Both), score));
    }

    [Fact]
    public void Without_enough_history_nothing_is_judged()
    {
        var score = AnomalyDetector.Score(1000, [100, 100]);
        Assert.Equal(AnomalyVerdict.NotEnoughHistory, AnomalyDetector.Judge(AnomalyRule.Create("rps", Direction.Both), score));
    }

    [Fact]
    public void Traffic_from_zero_counts_as_an_infinite_change()
    {
        var score = AnomalyDetector.Score(50, [0, 0, 0, 0, 0, 0, 0]);
        Assert.Equal(AnomalyVerdict.Anomalous, AnomalyDetector.Judge(AnomalyRule.Create("rps", Direction.Up), score));
    }
}
