#nullable enable
using System;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Platform.Windows.PushRelay;

/// <summary>Describes this Windows device to the relay: machine name and OS version, each at most 64 characters.</summary>
public sealed class WindowsPushRelayDeviceInfo : IPushRelayDeviceInfo
{
    private const int MaxLength = 64;

    public PushRelayDeviceDescriptor Describe()
        => new(PushRelayPlatforms.Windows,
            Truncate(GetMachineName()),
            Truncate("Windows " + Environment.OSVersion.Version));

    private static string? GetMachineName()
    {
        try
        {
            return Environment.MachineName;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        value = value.Trim();
        return value.Length <= MaxLength ? value : value[..MaxLength];
    }
}
