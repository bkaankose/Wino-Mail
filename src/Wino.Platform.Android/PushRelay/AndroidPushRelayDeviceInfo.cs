using Android.OS;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Platform.Android.PushRelay;

/// <summary>
/// Describes this phone or tablet to the relay: platform "android", the maker and model ("Google Pixel 8") and the
/// Android version. Read once; none of them changes while the app runs. Android 12+ hides the user-assigned device
/// name without a Bluetooth permission, so the model stands in for it.
/// </summary>
public sealed class AndroidPushRelayDeviceInfo : IPushRelayDeviceInfo
{
    private readonly Lazy<PushRelayDeviceDescriptor> _descriptor = new(Create);

    public PushRelayDeviceDescriptor Describe() => _descriptor.Value;

    private static PushRelayDeviceDescriptor Create()
    {
        var manufacturer = Build.Manufacturer?.Trim();
        var model = Build.Model?.Trim();
        string? name = model;
        if (!string.IsNullOrWhiteSpace(manufacturer) && !string.IsNullOrWhiteSpace(model) &&
            !model.StartsWith(manufacturer, StringComparison.OrdinalIgnoreCase))
        {
            name = $"{char.ToUpperInvariant(manufacturer[0])}{manufacturer[1..]} {model}";
        }

        var release = Build.VERSION.Release;
        return new PushRelayDeviceDescriptor(PushRelayPlatforms.Android,
            string.IsNullOrWhiteSpace(name) ? null : name,
            string.IsNullOrWhiteSpace(release) ? null : "Android " + release);
    }
}
