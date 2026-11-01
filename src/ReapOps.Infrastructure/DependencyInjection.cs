using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReapOps.Application.Diagnosis;
using ReapOps.Application.Ports;
using ReapOps.Infrastructure.Agent;
using ReapOps.Infrastructure.Notifications;
using ReapOps.Infrastructure.Persistence;
using ReapOps.Infrastructure.Signals;

namespace ReapOps.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

public static class DependencyInjection
{
    public static IServiceCollection AddReapOpsInfrastructure(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddSingleton(Database.Create(cfg.GetConnectionString("Postgres")));
        services.AddSingleton<IncidentRepository>();
        services.AddSingleton<IIncidentReader>(sp => sp.GetRequiredService<IncidentRepository>());
        services.AddSingleton<IIncidentStore>(sp => sp.GetRequiredService<IncidentRepository>());
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<DiagnosisRepository>();
        services.AddSingleton<IDiagnosisReader>(sp => sp.GetRequiredService<DiagnosisRepository>());
        return services;
    }

    public static IServiceCollection AddReapOpsSignals(this IServiceCollection services)
    {
        services.AddSingleton<IMetricsReader, PrometheusReader>();
        services.AddSingleton<IClusterReader, KubernetesReader>();
        services.AddSingleton<INotifier, EmailNotifier>();
        return services;
    }

    public static IServiceCollection AddReapOpsAgent(this IServiceCollection services)
    {
        services.AddSingleton<IDiagnosisStore>(sp => sp.GetRequiredService<DiagnosisRepository>());
        services.AddSingleton<IDiagnosisModel, UnconfiguredModel>();
        services.AddSingleton<IAgentTool, QueryMetricsTool>();
        services.AddSingleton<IAgentTool, WorkloadStatusTool>();
        services.AddSingleton<IAgentTool, IncidentHistoryTool>();
        services.AddSingleton<DiagnoseIncident>();
        services.AddSingleton<EvaluateDiagnoses>();
        return services;
    }
}
