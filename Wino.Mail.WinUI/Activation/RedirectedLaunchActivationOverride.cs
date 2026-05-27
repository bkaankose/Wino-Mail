using System;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.WinUI.Activation;

internal sealed class RedirectedLaunchActivation
{
    public WinoApplicationMode Mode { get; init; }
    public string? LaunchArguments { get; init; }
    public string? TileId { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

internal static class RedirectedLaunchActivationOverride
{
    private const string PendingActivationKey = "PendingRedirectedLaunchActivation";
    private const string ModeKey = "Mode";
    private const string LaunchArgumentsKey = "LaunchArguments";
    private const string TileIdKey = "TileId";
    private const string CreatedAtUtcKey = "CreatedAtUtc";
    private static readonly TimeSpan PendingActivationLifetime = TimeSpan.FromSeconds(30);

    public static void QueueIfNeeded(AppActivationArguments activationArgs)
    {
        if (!TryCreate(activationArgs, out var activation))
            return;

        ApplicationData.Current.LocalSettings.Values[PendingActivationKey] = CreateCompositeValue(activation!);
    }

    public static bool TryConsume(AppActivationArguments activationArgs, out RedirectedLaunchActivation activation)
    {
        activation = null!;

        if (activationArgs.Kind != ExtendedActivationKind.Launch)
            return false;

        if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(PendingActivationKey, out var value) ||
            value is not ApplicationDataCompositeValue compositeValue)
        {
            return false;
        }

        Clear();

        try
        {
            var parsedActivation = ParseCompositeValue(compositeValue);
            if (parsedActivation == null ||
                DateTimeOffset.UtcNow - parsedActivation.CreatedAtUtc > PendingActivationLifetime)
            {
                return false;
            }

            activation = parsedActivation;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryCreate(AppActivationArguments activationArgs, out RedirectedLaunchActivation? activation)
    {
        activation = null;

        if (activationArgs.Kind != ExtendedActivationKind.Launch ||
            activationArgs.Data is not ILaunchActivatedEventArgs launchArgs ||
            !AppModeActivationResolver.TryResolveExplicit(
                launchArgs.Arguments,
                launchArgs.TileId,
                Environment.CommandLine,
                out var mode))
        {
            return false;
        }

        activation = new RedirectedLaunchActivation
        {
            Mode = mode,
            LaunchArguments = EnsureModeLaunchArgument(launchArgs.Arguments, mode),
            TileId = launchArgs.TileId
        };

        return true;
    }

    private static ApplicationDataCompositeValue CreateCompositeValue(RedirectedLaunchActivation activation)
    {
        return new ApplicationDataCompositeValue
        {
            [ModeKey] = activation.Mode.ToString(),
            [LaunchArgumentsKey] = activation.LaunchArguments ?? string.Empty,
            [TileIdKey] = activation.TileId ?? string.Empty,
            [CreatedAtUtcKey] = activation.CreatedAtUtc.ToString("o")
        };
    }

    private static RedirectedLaunchActivation? ParseCompositeValue(ApplicationDataCompositeValue compositeValue)
    {
        if (!Enum.TryParse(compositeValue[ModeKey]?.ToString(), ignoreCase: true, out WinoApplicationMode mode) ||
            !DateTimeOffset.TryParse(compositeValue[CreatedAtUtcKey]?.ToString(), out var createdAtUtc))
        {
            return null;
        }

        return new RedirectedLaunchActivation
        {
            Mode = mode,
            LaunchArguments = GetOptionalCompositeString(compositeValue, LaunchArgumentsKey),
            TileId = GetOptionalCompositeString(compositeValue, TileIdKey),
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

    private static string EnsureModeLaunchArgument(string? launchArguments, WinoApplicationMode mode)
    {
        return string.IsNullOrWhiteSpace(launchArguments)
            ? AppEntryConstants.GetModeLaunchArgument(mode)
            : launchArguments;
    }

    private static void Clear()
        => ApplicationData.Current.LocalSettings.Values.Remove(PendingActivationKey);
}
