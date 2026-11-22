using System.ComponentModel;
using System.Text.Json;
using Wino.Core.Domain.Interfaces;
using Wino.Platform.Linux.Desktop;
using Wino.Services;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// The desktop profile: what the session is (<see cref="LinuxDesktopEnvironment"/>), the window style the user chose
/// (Automatic, Standard, Tiling) and whether the Omarchy theme is followed. Both settings are head-local keys of
/// <see cref="IConfigurationService"/>. Main thread only; <see cref="Changed"/> fires after any of them changed.
/// </summary>
internal sealed class LinuxDesktopProfile : INotifyPropertyChanged
{
    public const string WindowStyleKey = "LinuxWindowStyle";
    public const string FollowOmarchyThemeKey = "LinuxFollowOmarchyTheme";
    public const int AutomaticIndex = 0;
    public const int StandardIndex = 1;
    public const int TilingIndex = 2;

    private readonly IConfigurationService _configuration;
    private LinuxDesktopEnvironment _environment;

    public LinuxDesktopProfile(IConfigurationService configuration)
    {
        _configuration = configuration;
        _environment = LinuxDesktopEnvironment.Detect();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Changed;

    public LinuxDesktopEnvironment Environment => _environment;

    public int WindowStyleIndex
    {
        get => ClampIndex(_configuration.Get(WindowStyleKey, AutomaticIndex));
        set
        {
            value = ClampIndex(value);
            if (value == WindowStyleIndex && _configuration.Contains(WindowStyleKey)) return;
            _configuration.Set(WindowStyleKey, value);
            Raise(nameof(WindowStyleIndex));
        }
    }

    public bool FollowOmarchyTheme
    {
        get => _configuration.Get(FollowOmarchyThemeKey, true);
        set
        {
            if (value == FollowOmarchyTheme && _configuration.Contains(FollowOmarchyThemeKey)) return;
            _configuration.Set(FollowOmarchyThemeKey, value);
            Raise(nameof(FollowOmarchyTheme));
        }
    }

    public LinuxWindowStyle EffectiveStyle
#if DEBUG
        => DebugStyleOverride ?? Resolve(WindowStyleIndex, _environment.DetectedStyle);
#else
        => Resolve(WindowStyleIndex, _environment.DetectedStyle);
#endif

    /// <summary>The effective style from <c>preferences.json</c> before the services exist (the first window is built before them).</summary>
    public static LinuxWindowStyle LoadEffectiveStyle(string applicationDataRoot)
    {
        var detected = LinuxDesktopEnvironment.Detect().DetectedStyle;
        // Never create the data folder here (see WindowStateStore.LoadPlacement).
        if (!File.Exists(Path.Combine(applicationDataRoot, "preferences.json"))) return detected;
        try
        {
            return Resolve(ClampIndex(new JsonFileConfigurationService(applicationDataRoot).Get(WindowStyleKey, AutomaticIndex)), detected);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Serilog.Log.Warning(exception, "The window style could not be read.");
            return detected;
        }
    }

    private static LinuxWindowStyle Resolve(int index, LinuxWindowStyle detected) => index switch
    {
        StandardIndex => LinuxWindowStyle.Standard,
        TilingIndex => LinuxWindowStyle.Tiling,
        _ => detected
    };

    private static int ClampIndex(int index) => index is >= AutomaticIndex and <= TilingIndex ? index : AutomaticIndex;

    private void Raise(string property)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        Changed?.Invoke();
    }

#if DEBUG
    private LinuxDesktopEnvironment? _detected;

    public LinuxWindowStyle? DebugStyleOverride { get; private set; }

    public string? DebugOmarchySource { get; private set; }

    public void DebugSetStyle(LinuxWindowStyle? style)
    {
        DebugStyleOverride = style;
        Changed?.Invoke();
    }

    /// <summary>Treats <paramref name="currentFolder"/> as Omarchy's <c>current</c> folder; null or empty restores detection.</summary>
    public void DebugSetOmarchySource(string? currentFolder)
    {
        _detected ??= _environment;
        DebugOmarchySource = string.IsNullOrEmpty(currentFolder) ? null : currentFolder;
        _environment = DebugOmarchySource is { } folder
            ? _detected with { IsOmarchy = true, OmarchyCurrentFolder = folder }
            : _detected;
        Raise(nameof(Environment));
    }
#endif
}
