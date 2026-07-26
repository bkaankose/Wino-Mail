using System.Text.RegularExpressions;

namespace Wino.Services;

public static partial class DiagnosticLogRedactor
{
    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var redacted = EmailAddressRegex().Replace(value, "[redacted-email]");
        redacted = BearerTokenRegex().Replace(redacted, "Bearer [redacted-token]");
        redacted = SensitiveAssignmentRegex().Replace(redacted, "$1=[redacted]");
        redacted = WindowsPathRegex().Replace(redacted, "[redacted-path]");
        redacted = UrlDetailsRegex().Replace(redacted, "$1[redacted-url-details]");
        return redacted;
    }

    [GeneratedRegex(@"(?<![\w.+-])[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}(?![\w.-])", RegexOptions.CultureInvariant)]
    private static partial Regex EmailAddressRegex();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"\b(password|token|secret|authorization|refresh_token|access_token)\s*[:=]\s*(?:Bearer\s+)?[^\s,;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveAssignmentRegex();

    [GeneratedRegex(@"(?<![\w])(?:[A-Za-z]:\\|\\\\)[^\r\n,;]*?(?=\s+\w+=|[,;]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(@"(https?://[^/\s?#]+)(?:[/][^\s?#]*)?(?:\?[^\s#]*)?(?:#[^\s]*)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlDetailsRegex();
}
