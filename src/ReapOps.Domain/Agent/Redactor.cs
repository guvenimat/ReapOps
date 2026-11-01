using System.Text.RegularExpressions;

namespace ReapOps.Domain.Agent;

public static partial class Redactor
{
    [GeneratedRegex(@"(?i)(password|passwd|pwd|secret|token|api[_-]?key)(\s*[=:]\s*|""\s*:\s*"")[^\s;,""']+")]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"(?i)(authorization\s*[:=]\s*)[^\r\n]+")]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(@"(?i)(bearer|basic)\s+[a-z0-9\-._~+/]+=*")]
    private static partial Regex Bearer();

    [GeneratedRegex(@"\b(sk|pk|ck|rk|whsec|sk_test|sk_live)[-_][A-Za-z0-9_\-]{8,}")]
    private static partial Regex PrefixedKey();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"(?<!\d)(\+?\d[\d \-]{8,}\d)(?!\d)")]
    private static partial Regex Phone();

    [GeneratedRegex(@"\b\d{4}[ \-]?\d{4}[ \-]?\d{4}[ \-]?\d{4}\b")]
    private static partial Regex Card();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = AuthorizationHeader().Replace(text, m => $"{m.Groups[1].Value}[redacted]");
        s = Bearer().Replace(s, m => $"{m.Groups[1].Value} [redacted]");
        s = KeyValue().Replace(s, m => $"{m.Groups[1].Value}{m.Groups[2].Value}[redacted]");
        s = PrefixedKey().Replace(s, "[redacted key]");
        s = Card().Replace(s, "[redacted card]");
        s = Email().Replace(s, "[redacted email]");
        s = Phone().Replace(s, "[redacted phone]");
        return s;
    }
}
