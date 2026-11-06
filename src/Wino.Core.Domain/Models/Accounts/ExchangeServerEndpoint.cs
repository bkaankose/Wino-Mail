using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Accounts;

public static class ExchangeServerEndpoint
{
    public static Uri? Normalize(string value, ExchangeTransport transport)
    {
        var candidate = value.Contains("://", StringComparison.Ordinal) ? value.Trim() : "https://" + value.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return null;

        var defaultPath = transport == ExchangeTransport.ActiveSync ? "/Microsoft-Server-ActiveSync" : "/EWS/Exchange.asmx";
        return uri.AbsolutePath is "" or "/" ||
            uri.AbsolutePath.Equals("/EWS/Exchange.asmx", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Equals("/Microsoft-Server-ActiveSync", StringComparison.OrdinalIgnoreCase)
                ? new Uri(uri.GetLeftPart(UriPartial.Authority) + defaultPath)
                : uri;
    }
}
