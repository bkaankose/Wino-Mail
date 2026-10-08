using System;

namespace Wino.Core.Activation;

public static class WinoProtocolActivationResolver
{
    public static bool IsBillingSuccess(Uri? uri)
    {
        return uri != null &&
               string.Equals(uri.Scheme, "wino", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(uri.Host, "billing", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(uri.AbsolutePath, "/success", StringComparison.OrdinalIgnoreCase) &&
               string.IsNullOrEmpty(uri.Query) &&
               string.IsNullOrEmpty(uri.Fragment);
    }
}
