using System.Reflection;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

[module: Dapper.DapperAot]

namespace ReapOps.Infrastructure.Persistence;

public static class Database
{
    const long MigrationLock = 0x52454F50;

    public sealed record VersionRecord(string Version);

    public static NpgsqlDataSource Create(string? connectionString)
    {
        var builder = new NpgsqlSlimDataSourceBuilder(connectionString ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not set"));
        builder.EnableArrays();
        return builder.Build();
    }

    public static async Task<int> MigrateAsync(NpgsqlDataSource db, ILogger log, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("SELECT pg_advisory_lock(@Key)", new LockRecord(MigrationLock));
        try
        {
            await conn.ExecuteAsync("CREATE TABLE IF NOT EXISTS schema_migrations (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())");
            var applied = (await conn.QueryAsync<string>("SELECT version FROM schema_migrations")).ToHashSet();
            var assembly = Assembly.GetExecutingAssembly();
            var count = 0;
            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("Migrations.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                var version = name["Migrations.".Length..^".sql".Length];
                if (applied.Contains(version)) continue;
                await using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                var sql = await reader.ReadToEndAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                await conn.ExecuteAsync(sql, transaction: tx);
                await conn.ExecuteAsync("INSERT INTO schema_migrations (version) VALUES (@Version)", new VersionRecord(version), tx);
                await tx.CommitAsync(ct);
                log.LogInformation("Applied migration {Version}", version);
                count++;
            }
            return count;
        }
        finally
        {
            await conn.ExecuteAsync("SELECT pg_advisory_unlock(@Key)", new LockRecord(MigrationLock));
        }
    }

    public sealed record LockRecord(long Key);
}
