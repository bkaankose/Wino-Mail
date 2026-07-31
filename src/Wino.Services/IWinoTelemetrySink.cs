using System.Collections.Generic;
using Sentry;

namespace Wino.Services;

public interface IWinoTelemetrySink
{
    void AddBreadcrumb(string message, IReadOnlyDictionary<string, string> data);

    void CaptureEvent(SentryEvent sentryEvent);
}

public sealed class SentryWinoTelemetrySink : IWinoTelemetrySink
{
    public void AddBreadcrumb(string message, IReadOnlyDictionary<string, string> data)
        => SentrySdk.AddBreadcrumb(
            message,
            category: "telemetry",
            data: new Dictionary<string, string>(data, System.StringComparer.Ordinal));

    public void CaptureEvent(SentryEvent sentryEvent)
        => SentrySdk.CaptureEvent(sentryEvent);
}
