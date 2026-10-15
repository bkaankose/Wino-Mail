using Microsoft.Identity.Client;

namespace Wino.Authentication;

/// <summary>
/// Sends MSAL's own diagnostics to wino.log (source context "MSAL"), without PII. The Outlook authenticator swallows
/// MsalUiRequired and MsalClient exceptions, so without this the log only ever shows the resulting attention state.
/// Every platform's <see cref="IOutlookAuthenticationHost"/> uses it.
/// </summary>
public static class MsalLogForwarding
{
#if DEBUG
    private const LogLevel MsalLogLevel = LogLevel.Verbose;
#else
    private const LogLevel MsalLogLevel = LogLevel.Info;
#endif

    private static readonly Serilog.ILogger MsalLogger = Serilog.Log.ForContext("SourceContext", "MSAL");

    public static PublicClientApplicationBuilder WithWinoLogging(this PublicClientApplicationBuilder builder)
        => builder.WithLogging(Forward, MsalLogLevel, enablePiiLogging: false, enableDefaultPlatformLogging: false);

    private static void Forward(LogLevel level, string message, bool containsPii)
    {
        if (containsPii) return;
        switch (level)
        {
            case LogLevel.Error: MsalLogger.Error("{Message}", message); break;
            case LogLevel.Warning: MsalLogger.Warning("{Message}", message); break;
            case LogLevel.Info: MsalLogger.Debug("{Message}", message); break;
            default: MsalLogger.Verbose("{Message}", message); break;
        }
    }
}
