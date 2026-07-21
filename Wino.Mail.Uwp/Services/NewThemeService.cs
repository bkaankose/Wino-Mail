using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Personalization;
using Wino.Mail.Uwp.Models.Personalization;
using Wino.Mail.Uwp.Theming;

namespace Wino.Services;

/// <summary>
/// Single-window UWP theme service. Only material effects supported by WinUI 2/UWP
/// are exposed: Mica, HostBackdrop Acrylic, solid color and image themes.
/// </summary>
public sealed class NewThemeService : INewThemeService
{
    public const string CustomThemeFolderName = "CustomThemes";
    private const string MetadataFileName = "themes.json";
    private const string BackdropKey = "WindowBackdropTypeKey";
    private const string ThemeKey = "RootTheme";
    private const string AccentKey = "AccentColor";
    private const string CurrentThemeKey = "CurrentApplicationThemeId";

    private readonly ApplicationDataContainer _settings = ApplicationData.Current.LocalSettings;
    private readonly UwpWindowPresentationManager windowPresentationManager;
    private readonly List<AppThemeBase> _builtInThemes =
    [
        new SystemAppTheme("Default", Guid.Empty),
        new PreDefinedAppTheme("Nighty", Guid.Parse("5b65e04e-fd7e-4c2d-8221-068d3e02d23a"), "#e1b12c", ApplicationElementTheme.Dark),
        new PreDefinedAppTheme("Forest", Guid.Parse("8bc89b37-a7c5-4049-86e2-de1ae8858dbd"), "#16a085", ApplicationElementTheme.Dark),
        new PreDefinedAppTheme("Clouds", Guid.Parse("3b621cc2-e270-4a76-8477-737917cccda0"), "#0984e3", ApplicationElementTheme.Light),
        new PreDefinedAppTheme("Snowflake", Guid.Parse("e143ddde-2e28-4846-9d98-dad63d6505f1"), "#4a69bd", ApplicationElementTheme.Light),
        new PreDefinedAppTheme("Garden", Guid.Parse("698e4466-f88c-4799-9c61-f0ea1308ed49"), "#05c46b", ApplicationElementTheme.Light)
    ];

    public event EventHandler<ApplicationElementTheme>? ElementThemeChanged;
    public event EventHandler<string>? AccentColorChanged;
    public event EventHandler<WindowBackdropType>? BackdropChanged;

    public NewThemeService(UwpWindowPresentationManager windowPresentationManager)
    {
        this.windowPresentationManager = windowPresentationManager;
    }

    public ApplicationElementTheme RootTheme
    {
        get => Enum.TryParse<ApplicationElementTheme>(_settings.Values[ThemeKey]?.ToString(), out var value)
            ? value
            : ApplicationElementTheme.Default;
        set
        {
            _settings.Values[ThemeKey] = value.ToString();
            windowPresentationManager.ApplyRootTheme(value);

            ElementThemeChanged?.Invoke(this, value);
            UpdateSystemCaptionButtonColors();
        }
    }

    public Guid? CurrentApplicationThemeId
    {
        get => Guid.TryParse(_settings.Values[CurrentThemeKey]?.ToString(), out var id) ? id : null;
        set => _settings.Values[CurrentThemeKey] = value?.ToString();
    }

    public string AccentColor
    {
        get => _settings.Values[AccentKey]?.ToString() ?? GetSystemAccentColorHex();
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? GetSystemAccentColorHex() : value;
            _settings.Values[AccentKey] = normalized;
            AccentColorChanged?.Invoke(this, normalized);
        }
    }

    public WindowBackdropType CurrentBackdropType
    {
        get => NormalizeBackdrop(_settings.Values[BackdropKey]?.ToString());
        set
        {
            var normalized = NormalizeBackdrop(value.ToString());
            _settings.Values[BackdropKey] = normalized.ToString();
            _settings.Values["BackdropKind"] = normalized switch
            {
                WindowBackdropType.Mica => UwpBackdropKind.Mica.ToString(),
                WindowBackdropType.DesktopAcrylic => UwpBackdropKind.Acrylic.ToString(),
                _ => UwpBackdropKind.Solid.ToString(),
            };
            ApplyBackdrop(normalized);
        }
    }

    public bool IsCustomTheme => CurrentApplicationThemeId is { } id && !_builtInThemes.Any(theme => theme.Id == id);

    public async Task InitializeAsync() => await ApplyThemeToActiveWindowAsync();

    public async Task<List<AppThemeBase>> GetAvailableThemesAsync()
    {
        var themes = new List<AppThemeBase>(_builtInThemes);
        themes.AddRange((await GetCurrentCustomThemesAsync()).Select(metadata => new CustomAppTheme(metadata)));
        return themes;
    }

    public async Task<CustomThemeMetadata> CreateNewCustomThemeAsync(string themeName, string accentColor, byte[] wallpaperData)
    {
        var metadata = new CustomThemeMetadata
        {
            Id = Guid.NewGuid(),
            Name = themeName,
            AccentColorHex = accentColor
        };

        var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(CustomThemeFolderName, CreationCollisionOption.OpenIfExists);
        var image = await folder.CreateFileAsync($"{metadata.Id}_preview.jpg", CreationCollisionOption.ReplaceExisting);
        await FileIO.WriteBytesAsync(image, wallpaperData);

        var themes = await GetCurrentCustomThemesAsync();
        themes.Add(metadata);
        await SaveCustomThemesAsync(folder, themes);
        return metadata;
    }

    public async Task<List<CustomThemeMetadata>> GetCurrentCustomThemesAsync()
    {
        var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(CustomThemeFolderName, CreationCollisionOption.OpenIfExists);
        var file = await folder.TryGetItemAsync(MetadataFileName) as StorageFile;
        if (file is null)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<CustomThemeMetadata>>(await FileIO.ReadTextAsync(file)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task<bool> DeleteCustomThemeAsync(Guid themeId)
    {
        var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(CustomThemeFolderName, CreationCollisionOption.OpenIfExists);
        var themes = await GetCurrentCustomThemesAsync();
        var removed = themes.RemoveAll(theme => theme.Id == themeId) > 0;
        if (!removed)
        {
            return false;
        }

        if (await folder.TryGetItemAsync($"{themeId}_preview.jpg") is IStorageItem image)
        {
            await image.DeleteAsync();
        }

        await SaveCustomThemesAsync(folder, themes);
        if (CurrentApplicationThemeId == themeId)
        {
            CurrentApplicationThemeId = Guid.Empty;
            ApplyBackdrop(WindowBackdropType.Mica);
        }

        return true;
    }

    public List<string> GetAvailableAccountColors() =>
    ["#0078D4", "#16A085", "#E1B12C", "#C0392B", "#8E44AD", "#2C3E50", "#D35400", "#27AE60"];

    public async Task ApplyCustomThemeAsync(bool isInitializing)
    {
        if (windowPresentationManager.RootFrame is null || CurrentApplicationThemeId is not { } id)
        {
            return;
        }

        var metadata = (await GetCurrentCustomThemesAsync()).FirstOrDefault(theme => theme.Id == id);
        if (metadata is null)
        {
            return;
        }

        // The pre-activation Prepare pass already painted this wallpaper. Decoding a
        // fresh BitmapImage for the same file after the first frame flashes the
        // backdrop, so initialization only refreshes the accent color.
        if (!isInitializing || !windowPresentationManager.IsWallpaperApplied(id))
        {
            windowPresentationManager.ApplyBackdrop(UwpBackdropKind.Image, new ImageBrush
            {
                ImageSource = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri($"ms-appdata:///local/{CustomThemeFolderName}/{id}_preview.jpg")),
                Stretch = Stretch.UniformToFill
            }, id);
        }

        await SetAccentColorAsync(metadata.AccentColorHex);
    }

    public void ApplyBackdrop(WindowBackdropType backdropType)
    {
        var normalized = NormalizeBackdrop(backdropType.ToString());
        var kind = normalized switch
        {
            WindowBackdropType.Mica => UwpBackdropKind.Mica,
            WindowBackdropType.DesktopAcrylic => UwpBackdropKind.Acrylic,
            _ => UwpBackdropKind.Solid,
        };
        windowPresentationManager.ApplyBackdrop(kind);
        BackdropChanged?.Invoke(this, normalized);
        UpdateSystemCaptionButtonColors();
    }

    public Task SetAccentColorAsync(string hexColor, bool preserveTheme = true)
    {
        AccentColor = hexColor;
        return Task.CompletedTask;
    }

    public string GetSystemAccentColorHex()
    {
        var color = new UISettings().GetColorValue(UIColorType.Accent);
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    public void UpdateSystemCaptionButtonColors()
    {
        var theme = RootTheme switch
        {
            ApplicationElementTheme.Light => ElementTheme.Light,
            ApplicationElementTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        windowPresentationManager.ApplyCaptionButtonColors(theme);
    }

    public List<BackdropTypeWrapper> GetAvailableBackdropTypes() =>
    [
        new(WindowBackdropType.Mica, "Mica"),
        new(WindowBackdropType.DesktopAcrylic, "Acrylic"),
        new(WindowBackdropType.None, "Solid")
    ];

    public async Task ApplyThemeToActiveWindowAsync()
    {
        RootTheme = RootTheme;
        if (IsCustomTheme)
        {
            await ApplyCustomThemeAsync(true);
        }
        else
        {
            ApplyBackdrop(CurrentBackdropType);
        }
    }

    private static WindowBackdropType NormalizeBackdrop(string? persisted) => persisted switch
    {
        nameof(WindowBackdropType.Mica) or nameof(WindowBackdropType.MicaAlt) or "1" or "2" => WindowBackdropType.Mica,
        nameof(WindowBackdropType.DesktopAcrylic) or nameof(WindowBackdropType.AcrylicBase) or nameof(WindowBackdropType.AcrylicThin) or "3" or "4" or "5" => WindowBackdropType.DesktopAcrylic,
        nameof(WindowBackdropType.None) or "0" => WindowBackdropType.None,
        _ => WindowBackdropType.Mica
    };

    private static async Task SaveCustomThemesAsync(StorageFolder folder, List<CustomThemeMetadata> themes)
    {
        var file = await folder.CreateFileAsync(MetadataFileName, CreationCollisionOption.ReplaceExisting);
        await FileIO.WriteTextAsync(file, JsonSerializer.Serialize(themes));
    }
}
