using NetArchTest.Rules;

namespace ReapOps.UnitTests;

public class ArchitectureTests
{
    static readonly System.Reflection.Assembly Domain = typeof(Domain.Incidents.Incident).Assembly;
    static readonly System.Reflection.Assembly Application = typeof(Application.Detection.DetectIncidents).Assembly;
    static readonly System.Reflection.Assembly Infrastructure = typeof(Infrastructure.DependencyInjection).Assembly;

    static void Holds(TestResult result) =>
        Assert.True(result.IsSuccessful, "Offending types: " + string.Join(", ", result.FailingTypeNames ?? []));

    [Fact]
    public void Domain_depends_on_nothing() =>
        Holds(Types.InAssembly(Domain).ShouldNot().HaveDependencyOnAny(
            "ReapOps.Application", "ReapOps.Infrastructure", "ReapOps.Api", "ReapOps.Worker",
            "Microsoft.Extensions", "Microsoft.AspNetCore", "Npgsql", "Dapper", "System.Net.Http", "System.Text.Json").GetResult());

    [Fact]
    public void Application_knows_ports_not_adapters() =>
        Holds(Types.InAssembly(Application).ShouldNot().HaveDependencyOnAny(
            "ReapOps.Infrastructure", "ReapOps.Api", "ReapOps.Worker", "Microsoft.AspNetCore", "Npgsql", "Dapper", "System.Text.Json").GetResult());

    [Fact]
    public void Infrastructure_does_not_know_the_hosts() =>
        Holds(Types.InAssembly(Infrastructure).ShouldNot().HaveDependencyOnAny("ReapOps.Api", "ReapOps.Worker", "Microsoft.AspNetCore").GetResult());

    [Fact]
    public void Detection_reads_signals_and_never_reaches_an_adapter() =>
        Holds(Types.InAssembly(Application).That().ResideInNamespace("ReapOps.Application.Detection")
            .ShouldNot().HaveDependencyOnAny("ReapOps.Infrastructure", "System.Net.Mail", "System.Diagnostics.Process").GetResult());
}
