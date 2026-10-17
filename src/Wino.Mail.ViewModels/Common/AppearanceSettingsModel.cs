using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.ViewModels;

/// <summary>
/// The Personalization page's theme and accent state over <see cref="INewThemeService"/>, shared by the macOS, iOS and
/// Android pages. Platform pages derive from it for their own window backdrop or material switches.
/// </summary>
public class AppearanceSettingsModel : ObservableObject, IDisposable
{
    /// <summary>The accent colors offered on every platform, in display order.</summary>
    public static readonly string[] AccentPalette =
    [
        "#0078d7", "#00838c", "#e3008c", "#ca4f07", "#e81123", "#00819e", "#10893e", "#881798",
        "#c239b3", "#767676", "#e1b12c", "#16a085", "#0984e3", "#4a69bd", "#05c46b"
    ];

    private static readonly ApplicationElementTheme[] Themes = [ApplicationElementTheme.Default, ApplicationElementTheme.Light, ApplicationElementTheme.Dark];

    private readonly INewThemeService _themes;
    private string? _lastAccent;

    public AppearanceSettingsModel(INewThemeService themes)
    {
        _themes = themes;
        _themes.ElementThemeChanged += ThemeChanged;
        _themes.AccentColorChanged += AccentChanged;
        _lastAccent = string.IsNullOrEmpty(_themes.AccentColor) ? null : _themes.AccentColor;
    }

    /// <summary>0 follows the system, 1 is light, 2 is dark.</summary>
    public int ThemeIndex
    {
        get => Array.IndexOf(Themes, _themes.RootTheme);
        set
        {
            if (value < 0 || value >= Themes.Length || Themes[value] == _themes.RootTheme) return;
            _themes.RootTheme = Themes[value];
            OnRootThemeChanged();
            OnPropertyChanged();
        }
    }

    /// <summary>The overriding accent, or null when the system accent is used.</summary>
    public string? AccentHex
    {
        get => string.IsNullOrEmpty(_themes.AccentColor) ? null : _themes.AccentColor;
        set
        {
            if (string.Equals(value ?? string.Empty, _themes.AccentColor ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return;
            if (!string.IsNullOrEmpty(value)) _lastAccent = value;
            _themes.AccentColor = value ?? string.Empty;
        }
    }

    public bool UseSystemAccent
    {
        get => string.IsNullOrEmpty(_themes.AccentColor);
        set
        {
            if (value == UseSystemAccent) return;
            AccentHex = value ? null : _lastAccent ?? AccentPalette[0];
        }
    }

    /// <summary>Runs after a new root theme is stored, for platforms whose theme service does not apply it itself.</summary>
    protected virtual void OnRootThemeChanged() { }

    /// <summary>Refreshes every property after the platform reports an appearance change from elsewhere.</summary>
    protected virtual void NotifyAppearanceChanged()
    {
        OnPropertyChanged(nameof(ThemeIndex));
        OnPropertyChanged(nameof(AccentHex));
        OnPropertyChanged(nameof(UseSystemAccent));
    }

    private void ThemeChanged(object? sender, ApplicationElementTheme theme) => OnPropertyChanged(nameof(ThemeIndex));

    private void AccentChanged(object? sender, string accent)
    {
        OnPropertyChanged(nameof(AccentHex));
        OnPropertyChanged(nameof(UseSystemAccent));
    }

    public virtual void Dispose()
    {
        _themes.ElementThemeChanged -= ThemeChanged;
        _themes.AccentColorChanged -= AccentChanged;
        GC.SuppressFinalize(this);
    }
}
