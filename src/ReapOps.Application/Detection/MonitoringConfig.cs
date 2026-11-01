using System.Globalization;

namespace ReapOps.Application.Detection;

public sealed class MonitoringOptions
{
    public string ConsoleUrl { get; set; } = "http://localhost:8090";
    public int IntervalSeconds { get; set; } = 30;
    public List<MonitoredService> Services { get; set; } = [];
}

public sealed class MonitoredService
{
    public string Name { get; set; } = "";
    public string? Namespace { get; set; }
    public List<SloDefinition> Slos { get; set; } = [];
    public List<AnomalyDefinition> Anomalies { get; set; } = [];
}

public sealed class AnomalyDefinition
{
    public string Name { get; set; } = "";
    public string Query { get; set; } = "";
    public string Direction { get; set; } = "both";
    public double? Threshold { get; set; }
    public double? MinRelativeChange { get; set; }
    public int? MinHistory { get; set; }
    public int SeasonDays { get; set; } = 7;
}

public sealed class SloDefinition
{
    public string Name { get; set; } = "";
    public double Objective { get; set; }
    public string ErrorRatioQuery { get; set; } = "";
}

public static class PromWindow
{
    public static string Format(TimeSpan window) =>
        window.TotalDays >= 1 && window.TotalDays % 1 == 0 ? $"{(int)window.TotalDays}d"
        : window.TotalHours >= 1 && window.TotalHours % 1 == 0 ? $"{(int)window.TotalHours}h"
        : $"{(int)window.TotalMinutes}m";

    public static string Render(string template, TimeSpan window) =>
        template.Replace("{window}", Format(window), StringComparison.Ordinal);

    public static string Percent(double ratio) => (ratio * 100).ToString("0.###", CultureInfo.InvariantCulture) + "%";
}
