using System;
using System.IO;
using System.Linq;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.WinUI.Activation;

internal enum PendingBootstrapActivationKind
{
    Launch,
    Protocol,
    File
}

internal sealed class PendingBootstrapActivation
{
    public PendingBootstrapActivationKind Kind { get; init; }
    public WinoApplicationMode Mode { get; init; } = WinoApplicationMode.Mail;
    public string? LaunchArguments { get; init; }
    public string? TileId { get; init; }
    public string? ProtocolUri { get; init; }
    public string[] FilePaths { get; init; } = [];
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

internal static class CalendarEntryBootstrapActivation
{
    private const string PendingActivationKey = "PendingCalendarEntryBootstrapActivation";
    private const string KindKey = "Kind";
    private const string ModeKey = "Mode";
    private const string LaunchArgumentsKey = "LaunchArguments";
    private const string TileIdKey = "TileId";
    private const string ProtocolUriKey = "ProtocolUri";
    private const string FilePathsKey = "FilePaths";
    private const string CreatedAtUtcKey = "CreatedAtUtc";
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
        var compositeValue = new ApplicationDataCompositeValue
        {
            [KindKey] = pendingActivation.Kind.ToString(),
            [ModeKey] = pendingActivation.Mode.ToString(),
            [LaunchArgumentsKey] = pendingActivation.LaunchArguments ?? string.Empty,
            [TileIdKey] = pendingActivation.TileId ?? string.Empty,
            [ProtocolUriKey] = pendingActivation.ProtocolUri ?? string.Empty,
            [FilePathsKey] = string.Join("\n", pendingActivation.FilePaths),
            [CreatedAtUtcKey] = pendingActivation.CreatedAtUtc.ToString("o")
        };

        return compositeValue;
    }

    private static PendingBootstrapActivation? ParseCompositeValue(ApplicationDataCompositeValue compositeValue)
    {
        if (!Enum.TryParse(compositeValue[KindKey]?.ToString(), ignoreCase: true, out PendingBootstrapActivationKind kind) ||
            !Enum.TryParse(compositeValue[ModeKey]?.ToString(), ignoreCase: true, out WinoApplicationMode mode) ||
            !DateTimeOffset.TryParse(compositeValue[CreatedAtUtcKey]?.ToString(), out var createdAtUtc))
        {
            return null;
        }

        return new PendingBootstrapActivation
        {
            Kind = kind,
            Mode = mode,
            LaunchArguments = GetOptionalCompositeString(compositeValue, LaunchArgumentsKey),
            TileId = GetOptionalCompositeString(compositeValue, TileIdKey),
            ProtocolUri = GetOptionalCompositeString(compositeValue, ProtocolUriKey),
            FilePaths = GetOptionalCompositeString(compositeValue, FilePathsKey)?
                .Split(['\n'], StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [],
            CreatedAtUtc = createdAtUtc
        };
    }

    private static string? GetOptionalCompositeString(ApplicationDataCompositeValue compositeValue, string key)
    {
        if (!compositeValue.TryGetValue(key, out var value))
            return null;

        var stringValue = value?.ToString();
        return string.IsNullOrWhiteSpace(stringValue) ? null : stringValue;
    }

    public static bool LaunchMailHost()
    {
        var mailAppUserModelId = AppEntryConstants.GetAppUserModelId(WinoApplicationMode.Mail);
        var appEntries = Package.Current.GetAppListEntriesAsync().AsTask().GetAwaiter().GetResult();
        var mailEntry = appEntries.FirstOrDefault(entry =>
            string.Equals(entry.AppUserModelId, mailAppUserModelId, StringComparison.OrdinalIgnoreCase));

        return mailEntry != null && mailEntry.LaunchAsync().AsTask().GetAwaiter().GetResult();
    }

    private static bool TryCreatePendingActivation(AppActivationArguments activationArgs, out PendingBootstrapActivation? pendingActivation)
    {
        pendingActivation = null;

        if (activationArgs.Kind == ExtendedActivationKind.Launch &&
            activationArgs.Data is ILaunchActivatedEventArgs launchArgs)
        {
            var resolvedMode = AppModeActivationResolver.Resolve(launchArgs.Arguments, launchArgs.TileId, Environment.CommandLine);
            if (resolvedMode != WinoApplicationMode.Calendar)
                return false;

            pendingActivation = new PendingBootstrapActivation
            {
                Kind = PendingBootstrapActivationKind.Launch,
                Mode = resolvedMode,
                LaunchArguments = launchArgs.Arguments,
                TileId = launchArgs.TileId
            };

            return true;
        }

        if (activationArgs.Kind == ExtendedActivationKind.Protocol &&
            activationArgs.Data is IProtocolActivatedEventArgs protocolArgs &&
            protocolArgs.Uri != null &&
            (string.Equals(protocolArgs.Uri.Scheme, "webcal", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(protocolArgs.Uri.Scheme, "webcals", StringComparison.OrdinalIgnoreCase)))
        {
            pendingActivation = new PendingBootstrapActivation
            {
                Kind = PendingBootstrapActivationKind.Protocol,
                Mode = WinoApplicationMode.Calendar,
                ProtocolUri = protocolArgs.Uri.AbsoluteUri
            };

            return true;
        }

        if (activationArgs.Kind == ExtendedActivationKind.File &&
            activationArgs.Data is IFileActivatedEventArgs fileArgs)
        {
            var filePaths = fileArgs.Files?
                .OfType<IStorageItem>()
                .Where(item => string.Equals(Path.GetExtension(item.Path), ".ics", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (filePaths == null || filePaths.Length == 0)
                return false;

            pendingActivation = new PendingBootstrapActivation
            {
                Kind = PendingBootstrapActivationKind.File,
                Mode = WinoApplicationMode.Calendar,
                FilePaths = filePaths
            };

            return true;
        }

        return false;
    }
}
