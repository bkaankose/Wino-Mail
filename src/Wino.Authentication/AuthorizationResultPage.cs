using System.IO;
using System.Net;
using System.Reflection;

namespace Wino.Authentication;

/// <summary>
/// Renders the page the browser shows after the loopback redirect. The template and the Wino
/// logo are embedded so the authentication assembly stays free of UI framework references.
/// </summary>
internal static class AuthorizationResultPage
{
    private const string TemplateResourceName = "Wino.Authentication.Assets.AuthorizationResult.html";
    private const string LogoResourceName = "Wino.Authentication.Assets.WinoLogo.svg";

    private const string SuccessIcon = "<path d=\"M3.5 8.5l3 3 6-7\"/>";
    private const string ErrorIcon = "<path d=\"M4.5 4.5l7 7M11.5 4.5l-7 7\"/>";

    private static readonly string Template = ReadResource(TemplateResourceName);
    private static readonly string Logo = ReadResource(LogoResourceName);

    public static string Render(string applicationDisplayName, string? error)
    {
        var appName = WebUtility.HtmlEncode(applicationDisplayName);
        var succeeded = string.IsNullOrWhiteSpace(error);

        var heading = succeeded
            ? "You're signed in"
            : DescribeError(error!);
        var message = succeeded
            ? $"Google has granted access. You can close this tab and return to {appName}; setup continues there automatically."
            : $"Close this tab and return to {appName} to try again. Nothing was changed in your account.";

        return Template
            .Replace("{{title}}", $"{appName} · {(succeeded ? "Signed in" : "Sign-in failed")}")
            .Replace("{{state}}", succeeded ? "success" : "error")
            .Replace("{{logo}}", Logo)
            .Replace("{{badgeIcon}}", succeeded ? SuccessIcon : ErrorIcon)
            .Replace("{{badge}}", succeeded ? "Authorization complete" : "Authorization failed")
            .Replace("{{heading}}", WebUtility.HtmlEncode(heading))
            .Replace("{{message}}", message)
            .Replace("{{detail}}", succeeded ? string.Empty : WebUtility.HtmlEncode(error!))
            .Replace("{{appName}}", appName);
    }

    private static string DescribeError(string error) => error switch
    {
        "access_denied" => "Access was declined",
        "invalid_state" => "The sign-in response could not be verified",
        "invalid_code" => "Google did not return a sign-in code",
        _ => "Sign-in did not complete"
    };

    private static string ReadResource(string name)
    {
        using var stream = typeof(AuthorizationResultPage).Assembly.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"Embedded resource '{name}' is missing.");
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
