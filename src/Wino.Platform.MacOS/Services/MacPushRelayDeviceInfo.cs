using System.Runtime.InteropServices;
using CoreFoundation;
using Foundation;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Platform.MacOS.Services;

/// <summary>
/// Describes this Mac to the relay: the computer name from Sharing settings and the macOS version. Read once, because the
/// relay service asks on every pass and neither value changes while the app runs.
/// </summary>
public sealed class MacPushRelayDeviceInfo : IPushRelayDeviceInfo
{
    private const string SystemConfiguration = "/System/Library/Frameworks/SystemConfiguration.framework/SystemConfiguration";
    private readonly Lazy<PushRelayDeviceDescriptor> _descriptor = new(Create);

    public PushRelayDeviceDescriptor Describe() => _descriptor.Value;

    private static PushRelayDeviceDescriptor Create()
    {
        var name = ComputerName();
        if (string.IsNullOrWhiteSpace(name)) name = Environment.MachineName;

        var version = NSProcessInfo.ProcessInfo.OperatingSystemVersion;
        var osVersion = version.PatchVersion > 0
            ? $"macOS {version.Major}.{version.Minor}.{version.PatchVersion}"
            : $"macOS {version.Major}.{version.Minor}";
        return new PushRelayDeviceDescriptor(PushRelayPlatforms.MacOS, string.IsNullOrWhiteSpace(name) ? null : name, osVersion);
    }

    /// <summary>The user-visible computer name (System Settings › General › Sharing); null when unavailable.</summary>
    private static string? ComputerName()
    {
        try
        {
            var handle = SCDynamicStoreCopyComputerName(IntPtr.Zero, IntPtr.Zero);
            return handle == IntPtr.Zero ? null : CFString.FromHandle(handle, releaseHandle: true);
        }
        catch (Exception error)
        {
            Serilog.Log.Debug(error, "The computer name is unavailable.");
            return null;
        }
    }

    // CFStringRef SCDynamicStoreCopyComputerName(SCDynamicStoreRef store, CFStringEncoding *nameEncoding); returns +1.
    [DllImport(SystemConfiguration)]
    private static extern IntPtr SCDynamicStoreCopyComputerName(IntPtr store, IntPtr nameEncoding);
}
