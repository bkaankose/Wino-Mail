using System.Diagnostics.CodeAnalysis;
using Wino.Core.Activation;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.ViewModels.Activation;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>Turns what GApplication opened (an address or a file) into the shared activation kinds.</summary>
internal static class LinuxActivation
{
    public static ActivationItem? Classify(Gio.File file)
    {
        var uri = file.GetUri();
        if (string.IsNullOrEmpty(uri)) return null;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return null;
        Serilog.Log.Information("Activated with a {Scheme} address.", parsed.Scheme);
        return new ActivationItem(ActivationUriClassifier.Classify(parsed), uri);
    }

    /// <summary>A <c>wino://auth-complete</c> address: it goes to the waiting sign-in, never to activation routing.</summary>
    public static bool TryGetAuthCallback(Gio.File file, [NotNullWhen(true)] out Uri? callback)
    {
        callback = null;
        var uri = file.GetUri();
        if (string.IsNullOrEmpty(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return false;
        if (!WinoAccountExternalAuth.IsCallback(parsed)) return false;
        callback = parsed;
        return true;
    }
}
