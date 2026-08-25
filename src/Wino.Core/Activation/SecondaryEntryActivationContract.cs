#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Activation;

public enum PendingBootstrapActivationKind
{
    Launch,
    Protocol,
    File
}

public sealed class PendingBootstrapActivation
{
    public PendingBootstrapActivationKind Kind { get; init; }
    public WinoApplicationMode Mode { get; init; } = WinoApplicationMode.Mail;
    public string? LaunchArguments { get; init; }
    public string? TileId { get; init; }
    public string? ProtocolUri { get; init; }
    public string[] FilePaths { get; init; } = [];
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public static class SecondaryEntryActivationContract
{
    private const string KindKey = "Kind";
    private const string ModeKey = "Mode";
    private const string LaunchArgumentsKey = "LaunchArguments";
    private const string TileIdKey = "TileId";
    private const string ProtocolUriKey = "ProtocolUri";
    private const string FilePathsKey = "FilePaths";
    private const string CreatedAtUtcKey = "CreatedAtUtc";

    public static bool TryCreateLaunch(string? launchArguments,
                                       string? tileId,
                                       string? commandLine,
                                       out PendingBootstrapActivation? activation)
    {
        var mode = AppModeActivationResolver.Resolve(launchArguments, tileId, commandLine);
        if (mode is not (WinoApplicationMode.Calendar or WinoApplicationMode.Contacts))
        {
            activation = null;
            return false;
        }

        activation = new PendingBootstrapActivation
        {
            Kind = PendingBootstrapActivationKind.Launch,
            Mode = mode,
            LaunchArguments = launchArguments,
            TileId = tileId
        };

        return true;
    }

    public static bool TryCreateProtocol(Uri? uri, out PendingBootstrapActivation? activation)
    {
        activation = null;
        if (uri == null ||
            (!string.Equals(uri.Scheme, "webcal", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, "webcals", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        activation = new PendingBootstrapActivation
        {
            Kind = PendingBootstrapActivationKind.Protocol,
            Mode = WinoApplicationMode.Calendar,
            ProtocolUri = uri.AbsoluteUri
        };

        return true;
    }

    public static bool TryCreateFiles(IEnumerable<string?>? paths, out PendingBootstrapActivation? activation)
    {
        activation = null;
        var activationPaths = paths?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        var firstSupportedPath = activationPaths.FirstOrDefault(path => TryResolveFileMode(path, out _));

        if (firstSupportedPath == null || !TryResolveFileMode(firstSupportedPath, out var mode))
            return false;

        var matchingPaths = activationPaths
            .Where(path => TryResolveFileMode(path, out var pathMode) && pathMode == mode)
            .ToArray();

        activation = new PendingBootstrapActivation
        {
            Kind = PendingBootstrapActivationKind.File,
            Mode = mode,
            FilePaths = matchingPaths
        };

        return true;
    }

    public static bool TryResolveFileMode(string? path, out WinoApplicationMode mode)
    {
        mode = WinoApplicationMode.Mail;
        var extension = Path.GetExtension(path ?? string.Empty);

        if (string.Equals(extension, ".ics", StringComparison.OrdinalIgnoreCase))
        {
            mode = WinoApplicationMode.Calendar;
            return true;
        }

        if (string.Equals(extension, ".vcf", StringComparison.OrdinalIgnoreCase))
        {
            mode = WinoApplicationMode.Contacts;
            return true;
        }

        return false;
    }

    public static IReadOnlyDictionary<string, string> Serialize(PendingBootstrapActivation activation)
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KindKey] = activation.Kind.ToString(),
            [ModeKey] = activation.Mode.ToString(),
            [LaunchArgumentsKey] = activation.LaunchArguments ?? string.Empty,
            [TileIdKey] = activation.TileId ?? string.Empty,
            [ProtocolUriKey] = activation.ProtocolUri ?? string.Empty,
            [FilePathsKey] = string.Join("\n", activation.FilePaths),
            [CreatedAtUtcKey] = activation.CreatedAtUtc.ToString("o")
        };

    public static bool TryDeserialize(IReadOnlyDictionary<string, string?> values,
                                      out PendingBootstrapActivation? activation)
    {
        activation = null;
        if (!TryGetValue(values, KindKey, out var kindValue) ||
            !Enum.TryParse(kindValue, ignoreCase: true, out PendingBootstrapActivationKind kind) ||
            !TryGetValue(values, ModeKey, out var modeValue) ||
            !Enum.TryParse(modeValue, ignoreCase: true, out WinoApplicationMode mode) ||
            !TryGetValue(values, CreatedAtUtcKey, out var createdValue) ||
            !DateTimeOffset.TryParse(createdValue, out var createdAtUtc) ||
            !Enum.IsDefined(kind) ||
            mode is not (WinoApplicationMode.Calendar or WinoApplicationMode.Contacts))
        {
            return false;
        }

        activation = new PendingBootstrapActivation
        {
            Kind = kind,
            Mode = mode,
            LaunchArguments = GetOptionalValue(values, LaunchArgumentsKey),
            TileId = GetOptionalValue(values, TileIdKey),
            ProtocolUri = GetOptionalValue(values, ProtocolUriKey),
            FilePaths = GetOptionalValue(values, FilePathsKey)?
                .Split(['\n'], StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [],
            CreatedAtUtc = createdAtUtc
        };

        return kind switch
        {
            PendingBootstrapActivationKind.File => activation.FilePaths.Length > 0 &&
                                                   activation.FilePaths.All(path =>
                                                       TryResolveFileMode(path, out var pathMode) && pathMode == mode),
            PendingBootstrapActivationKind.Protocol => mode == WinoApplicationMode.Calendar,
            PendingBootstrapActivationKind.Launch => true,
            _ => false
        };
    }

    private static string? GetOptionalValue(IReadOnlyDictionary<string, string?> values, string key)
        => TryGetValue(values, key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static bool TryGetValue(IReadOnlyDictionary<string, string?> values, string key, out string? value)
    {
        if (values.TryGetValue(key, out value))
            return true;

        var pair = values.FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
        value = pair.Value;
        return pair.Key != null;
    }
}
