using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Serilog;
using Windows.ApplicationModel.Activation;
using MailCal.Core.Domain.Enums;
using WinRT;

namespace MailCal.Mail.WinUI.Activation;

/// <summary>
/// Reads <see cref="AppActivationArguments"/> defensively. The arguments are a COM proxy, and
/// reading <c>Kind</c> or <c>Data</c> fails with COMException 0x800706BA (RPC server unavailable)
/// once the activating server is gone. Callers then fall back to a standard launch.
/// </summary>
internal static class ActivationArgumentsReader
{
    public static AppActivationArguments? TryGetCurrent()
    {
        try
        {
            return AppInstance.GetCurrent().GetActivatedEventArgs();
        }
        catch (COMException ex)
        {
            Log.Warning(ex, "Activation arguments are unavailable. Continuing as a standard launch.");
            return null;
        }
    }

    public static bool TryGetKind(AppActivationArguments? activationArgs, out ExtendedActivationKind kind)
    {
        kind = ExtendedActivationKind.Launch;
        if (activationArgs == null)
            return false;

        try
        {
            kind = activationArgs.Kind;
            return true;
        }
        catch (COMException ex)
        {
            Log.Warning(ex, "Activation kind could not be read. Continuing as a standard launch.");
            return false;
        }
    }

    public static bool TryReadLaunch(AppActivationArguments? activationArgs, out string? arguments, out string? tileId)
    {
        arguments = null;
        tileId = null;

        if (activationArgs == null)
            return false;

        try
        {
            if (activationArgs.Kind != ExtendedActivationKind.Launch ||
                activationArgs.Data is not ILaunchActivatedEventArgs launchArgs)
            {
                return false;
            }

            arguments = launchArgs.Arguments;
            tileId = launchArgs.TileId;
            return true;
        }
        catch (COMException ex)
        {
            Log.Warning(ex, "Launch activation data could not be read. Continuing as a standard launch.");
            return false;
        }
    }
}

/// <summary>
/// Plain-data copy of a launch activation that waits for the migration window. The COM-backed
/// <see cref="AppActivationArguments"/> is not kept across the migration: by the time the user
/// clicks Launch its server can be gone. Only what the post-migration launch needs is copied.
/// </summary>
internal sealed record PendingMigrationActivation(
    ExtendedActivationKind Kind,
    string? NotificationArgument = null,
    IReadOnlyDictionary<string, string>? NotificationUserInput = null,
    string? ProtocolUri = null,
    MailCalApplicationMode FileMode = MailCalApplicationMode.Mail,
    IReadOnlyList<string>? FilePaths = null)
{
    public static PendingMigrationActivation StandardLaunch { get; } = new(ExtendedActivationKind.Launch);

    [DynamicWindowsRuntimeCast(typeof(AppNotificationActivatedEventArgs))]
    public static PendingMigrationActivation Capture(AppActivationArguments? activationArgs)
    {
        if (activationArgs == null)
            return StandardLaunch;

        try
        {
            var kind = activationArgs.Kind;

            if (kind == ExtendedActivationKind.AppNotification &&
                activationArgs.Data is AppNotificationActivatedEventArgs notificationArgs)
            {
                return new PendingMigrationActivation(
                    kind,
                    NotificationArgument: notificationArgs.Argument,
                    NotificationUserInput: notificationArgs.UserInput == null
                        ? null
                        : new Dictionary<string, string>(notificationArgs.UserInput));
            }

            if (kind == ExtendedActivationKind.Protocol &&
                activationArgs.Data is IProtocolActivatedEventArgs { Uri: { } protocolUri })
            {
                return new PendingMigrationActivation(kind, ProtocolUri: protocolUri.AbsoluteUri);
            }

            if (SecondaryEntryBootstrapActivation.TryGetSupportedFileActivation(activationArgs, out var fileMode, out var filePaths))
            {
                return new PendingMigrationActivation(kind, FileMode: fileMode, FilePaths: filePaths);
            }

            // Launch arguments travel separately in the in-process XAML LaunchActivatedEventArgs.
            // Share targets cannot be replayed after the migration.
            return new PendingMigrationActivation(kind);
        }
        catch (COMException ex)
        {
            Log.Warning(ex, "Activation arguments could not be captured before migration. Continuing as a standard launch.");
            return StandardLaunch;
        }
    }
}
