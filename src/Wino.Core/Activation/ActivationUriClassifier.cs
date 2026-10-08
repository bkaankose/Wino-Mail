#nullable enable

using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Activation;

/// <summary>What an operating-system activation URL asks Wino to do.</summary>
public enum ActivationUriKind
{
    Unsupported,
    MailTo,
    BillingSuccess,
    Webcal,
    CalendarFile,
    ContactFile
}

/// <summary>
/// Classifies the URLs a platform head receives from the OS (URL schemes and opened documents)
/// without touching services, so every head routes them the same way. <c>.eml</c> files are not
/// handled yet and classify as <see cref="ActivationUriKind.Unsupported"/>.
/// </summary>
public static class ActivationUriClassifier
{
    public static ActivationUriKind Classify(Uri? uri)
    {
        if (uri == null || !uri.IsAbsoluteUri)
            return ActivationUriKind.Unsupported;

        if (uri.IsFile)
            return ClassifyFilePath(uri.LocalPath);

        if (string.Equals(uri.Scheme, "mailto", StringComparison.OrdinalIgnoreCase))
            return ActivationUriKind.MailTo;

        if (WinoProtocolActivationResolver.IsBillingSuccess(uri))
            return ActivationUriKind.BillingSuccess;

        if (SecondaryEntryActivationContract.TryCreateProtocol(uri, out _))
            return ActivationUriKind.Webcal;

        return ActivationUriKind.Unsupported;
    }

    /// <summary>Classifies a URL string or an absolute file path.</summary>
    public static ActivationUriKind Classify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return ActivationUriKind.Unsupported;

        var trimmed = value.Trim();

        // A mailto URL without "//" is still absolute; Uri parses it, but a bare path must not be
        // mistaken for one, so absolute paths go straight to the file rules.
        if (trimmed.StartsWith('/'))
            return ClassifyFilePath(trimmed);

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            ? Classify(uri)
            : ActivationUriKind.Unsupported;
    }

    public static ActivationUriKind ClassifyFilePath(string? path)
    {
        if (!SecondaryEntryActivationContract.TryResolveFileMode(path, out var mode))
            return ActivationUriKind.Unsupported;

        return mode == WinoApplicationMode.Calendar ? ActivationUriKind.CalendarFile : ActivationUriKind.ContactFile;
    }

    /// <summary>The application mode an activation opens, or null when it opens Settings or nothing.</summary>
    public static WinoApplicationMode? GetMode(ActivationUriKind kind) => kind switch
    {
        ActivationUriKind.MailTo => WinoApplicationMode.Mail,
        ActivationUriKind.Webcal or ActivationUriKind.CalendarFile => WinoApplicationMode.Calendar,
        ActivationUriKind.ContactFile => WinoApplicationMode.Contacts,
        ActivationUriKind.BillingSuccess => WinoApplicationMode.Settings,
        _ => null
    };
}
