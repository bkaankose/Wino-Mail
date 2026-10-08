using System;
using System.Linq;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Wino.Core.Activation;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.WinUI.Activation;

internal static class SecondaryEntryBootstrapActivation
{
    private const string PendingActivationKey = "PendingCalendarEntryBootstrapActivation";
    private static readonly TimeSpan PendingActivationLifetime = TimeSpan.FromMinutes(1);

    public static bool ShouldBootstrapToMailHost(AppActivationArguments activationArgs)
        => TryCreatePendingActivation(activationArgs, out _);

    public static bool QueuePendingActivation(AppActivationArguments activationArgs)
    {
        if (!TryCreatePendingActivation(activationArgs, out var pendingActivation))
            return false;

        ApplicationData.Current.LocalSettings.Values[PendingActivationKey] = CreateCompositeValue(pendingActivation!);
        return true;
    }

    public static void ClearPendingActivation()
        => ApplicationData.Current.LocalSettings.Values.Remove(PendingActivationKey);

    public static PendingBootstrapActivation? ConsumePendingActivation()
    {
        if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(PendingActivationKey, out var pendingActivationValue) ||
            pendingActivationValue is not ApplicationDataCompositeValue compositeValue)
        {
            return null;
        }

        ClearPendingActivation();

        try
        {
            var pendingActivation = ParseCompositeValue(compositeValue);
            if (pendingActivation == null)
                return null;

            if (DateTimeOffset.UtcNow - pendingActivation.CreatedAtUtc > PendingActivationLifetime)
                return null;

            return pendingActivation;
        }
        catch
        {
            return null;
        }
    }

    private static ApplicationDataCompositeValue CreateCompositeValue(PendingBootstrapActivation pendingActivation)
    {
        var compositeValue = new ApplicationDataCompositeValue();
        foreach (var pair in SecondaryEntryActivationContract.Serialize(pendingActivation))
            compositeValue[pair.Key] = pair.Value;

        return compositeValue;
    }

    private static PendingBootstrapActivation? ParseCompositeValue(ApplicationDataCompositeValue compositeValue)
    {
        var values = compositeValue.ToDictionary(
            pair => pair.Key,
            pair => pair.Value?.ToString(),
            StringComparer.OrdinalIgnoreCase);

        return SecondaryEntryActivationContract.TryDeserialize(values, out var pendingActivation)
            ? pendingActivation
            : null;
    }

    public static bool LaunchMailHost()
    {
        var mailAppUserModelId = AppEntryConstants.GetAppUserModelId(WinoApplicationMode.Mail);
        var appEntries = Package.Current.GetAppListEntriesAsync().AsTask().GetAwaiter().GetResult();
        var mailEntry = appEntries.FirstOrDefault(entry =>
            string.Equals(entry.AppUserModelId, mailAppUserModelId, StringComparison.OrdinalIgnoreCase));

        return mailEntry != null && mailEntry.LaunchAsync().AsTask().GetAwaiter().GetResult();
    }

    internal static bool TryCreatePendingActivation(AppActivationArguments activationArgs, out PendingBootstrapActivation? pendingActivation)
    {
        pendingActivation = null;

        if (activationArgs.Kind == ExtendedActivationKind.Launch &&
            activationArgs.Data is ILaunchActivatedEventArgs launchArgs)
        {
            return SecondaryEntryActivationContract.TryCreateLaunch(
                launchArgs.Arguments,
                launchArgs.TileId,
                Environment.CommandLine,
                out pendingActivation);
        }

        if (activationArgs.Kind == ExtendedActivationKind.Protocol &&
            activationArgs.Data is IProtocolActivatedEventArgs protocolArgs &&
            protocolArgs.Uri != null)
        {
            return SecondaryEntryActivationContract.TryCreateProtocol(protocolArgs.Uri, out pendingActivation);
        }

        if (TryGetSupportedFileActivation(activationArgs, out var fileMode, out var filePaths))
        {
            pendingActivation = new PendingBootstrapActivation
            {
                Kind = PendingBootstrapActivationKind.File,
                Mode = fileMode,
                FilePaths = filePaths
            };

            return true;
        }

        return false;
    }

    internal static bool TryGetSupportedFileActivation(AppActivationArguments activationArgs,
                                                       out WinoApplicationMode mode,
                                                       out string[] filePaths)
    {
        mode = WinoApplicationMode.Mail;
        filePaths = [];

        if (activationArgs.Kind != ExtendedActivationKind.File ||
            activationArgs.Data is not IFileActivatedEventArgs fileArgs)
        {
            return false;
        }

        var activationPaths = fileArgs.Files?
            .OfType<IStorageItem>()
            .Select(item => item.Path)
            .ToArray();

        if (!SecondaryEntryActivationContract.TryCreateFiles(activationPaths, out var activation))
            return false;

        mode = activation!.Mode;
        filePaths = activation.FilePaths;
        return true;
    }
}
