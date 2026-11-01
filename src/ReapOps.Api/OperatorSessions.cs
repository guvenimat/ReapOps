using System.Security.Cryptography;
using System.Text;

namespace ReapOps.Api;

public sealed record Operator(string Name);

public sealed class OperatorSessions(IConfiguration cfg)
{
    public const string Cookie = "ro_session";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    readonly byte[] _key = Encoding.UTF8.GetBytes(cfg["Console:SigningKey"] ?? throw new InvalidOperationException("Console:SigningKey is not set"));
    readonly byte[] _password = Encoding.UTF8.GetBytes(cfg["Console:Password"] ?? throw new InvalidOperationException("Console:Password is not set"));

    public bool CheckPassword(string password) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(password)), SHA256.HashData(_password));

    public string Issue(string name, DateTime now)
    {
        var payload = $"{Convert.ToBase64String(Encoding.UTF8.GetBytes(name))}|{new DateTimeOffset(now + Lifetime).ToUnixTimeSeconds()}";
        return $"{payload}|{Sign(payload)}";
    }

    public Operator? Read(string? cookie, DateTime now)
    {
        if (string.IsNullOrEmpty(cookie)) return null;
        var parts = cookie.Split('|');
        if (parts.Length != 3) return null;
        var payload = $"{parts[0]}|{parts[1]}";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(payload)), Encoding.ASCII.GetBytes(parts[2]))) return null;
        if (!long.TryParse(parts[1], out var expires) || DateTimeOffset.FromUnixTimeSeconds(expires) < now) return null;
        try
        {
            return new Operator(Encoding.UTF8.GetString(Convert.FromBase64String(parts[0])));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    string Sign(string payload) => Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload)));
}

public sealed class OperatorAuthMiddleware(RequestDelegate next, OperatorSessions sessions)
{
    static readonly string[] Open = ["/api/auth/login", "/livez", "/healthz"];

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (path.StartsWith("/api/", StringComparison.Ordinal) && !Open.Contains(path))
        {
            if (sessions.Read(ctx.Request.Cookies[OperatorSessions.Cookie], DateTime.UtcNow) is not { } op)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            ctx.Items[nameof(Operator)] = op;
        }
        await next(ctx);
    }
}
