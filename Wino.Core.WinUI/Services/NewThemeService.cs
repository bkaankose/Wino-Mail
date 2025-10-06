using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI;
using CommunityToolkit.WinUI.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.UI;
using Windows.UI.ViewManagement;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Personalization;
using Wino.Core.WinUI;
using Wino.Core.WinUI.Extensions;
using Wino.Core.WinUI.Interfaces;
using Wino.Core.WinUI.Models.Personalization;
using Wino.Core.WinUI.Services;
using Wino.Messaging.Client.Shell;
using WinUIEx;

namespace Wino.Services;

/// <summary>
/// Next-generation theme service with enhanced WinUI support including backdrop management
/// </summary>
public class NewThemeService : INewThemeService
{
    public const string CustomThemeFolderName = "CustomThemes";

    private static string _cloudsThemeId = "3b621cc2-e270-4a76-8477-737917cccda0";
    private static string _forestThemeId = "8bc89b37-a7c5-4049-86e2-de1ae8858dbd";
    private static string _nightyThemeId = "5b65e04e-fd7e-4c2d-8221-068d3e02d23a";
    private static string _snowflakeThemeId = "e143ddde-2e28-4846-9d98-dad63d6505f1";
    private static string _gardenThemeId = "698e4466-f88c-4799-9c61-f0ea1308ed49";

    public event EventHandler<ApplicationElementTheme> ElementThemeChanged;
    public event EventHandler<string> AccentColorChanged;
    public event EventHandler<WindowBackdropType> BackdropChanged;

    private const string AccentColorKey = nameof(AccentColorKey);
    private const string CurrentApplicationThemeKey = nameof(CurrentApplicationThemeKey);
    private const string WindowBackdropTypeKey = nameof(WindowBackdropTypeKey);

    // Custom theme
    public const string CustomThemeAccentColorKey = nameof(CustomThemeAccentColorKey);

    // Keep reference so it does not get optimized/garbage collected
    private readonly UISettings uiSettings = new UISettings();

    private readonly IConfigurationService _configurationService;
    private readonly IUnderlyingThemeService _underlyingThemeService;
    private readonly IApplicationResourceManager<ResourceDictionary> _applicationResourceManager;

    private List<AppThemeBase> preDefinedThemes { get; set; } = new List<AppThemeBase>()
    {
        new PreDefinedAppTheme("Nighty", Guid.Parse(_nightyThemeId), "#e1b12c", ApplicationElementTheme.Dark),
        new PreDefinedAppTheme("Forest", Guid.Parse(_forestThemeId), "#16a085", ApplicationElementTheme.Dark),
        new PreDefinedAppTheme("Clouds", Guid.Parse(_cloudsThemeId), "#0984e3", ApplicationElementTheme.Light),
        new PreDefinedAppTheme("Snowflake", Guid.Parse(_snowflakeThemeId), "#4a69bd", ApplicationElementTheme.Light),
        new PreDefinedAppTheme("Garden", Guid.Parse(_gardenThemeId), "#05c46b", ApplicationElementTheme.Light),
    };

    public NewThemeService(IConfigurationService configurationService,
                          IUnderlyingThemeService underlyingThemeService,
                          IApplicationResourceManager<ResourceDictionary> applicationResourceManager)
    {
        _configurationService = configurationService;
        _underlyingThemeService = underlyingThemeService;
        _applicationResourceManager = applicationResourceManager;
    }

    /// <summary>
    /// Gets or sets (with LocalSettings persistence) the RequestedTheme of the root element.
    /// </summary>
    public ApplicationElementTheme RootTheme
    {
        get
        {
            return GetShellRootContent().RequestedTheme.ToWinoElementTheme();
        }
        set
        {
            GetShellRootContent().RequestedTheme = value.ToWindowsElementTheme();

            _configurationService.Set(UnderlyingThemeService.SelectedAppThemeKey, value);

            UpdateSystemCaptionButtonColors();

            // PopupRoot usually needs to react to changes.
            NotifyThemeUpdate();
        }
    }

    private Guid? currentApplicationThemeId;

    public Guid? CurrentApplicationThemeId
    {
        get { return currentApplicationThemeId; }
        set
        {
            currentApplicationThemeId = value;

            _configurationService.Set(CurrentApplicationThemeKey, value);

            if (WinoApplication.MainWindow != null)
            {
                WinoApplication.MainWindow.DispatcherQueue.TryEnqueue(async () =>
                {
                    await ApplyCustomThemeAsync(false);
                });
            }
        }
    }

    private string accentColor;

    public string AccentColor
    {
        get { return accentColor; }
        set
        {
            accentColor = value;

            UpdateAccentColor(value);

            _configurationService.Set(AccentColorKey, value);
            AccentColorChanged?.Invoke(this, value);
        }
    }

    private WindowBackdropType currentBackdropType;

    public WindowBackdropType CurrentBackdropType
    {
        get { return currentBackdropType; }
        set
        {
            // Only update if the backdrop type has actually changed
            if (currentBackdropType == value) return;

            currentBackdropType = value;
            _configurationService.Set(WindowBackdropTypeKey, (int)value);

            if (WinoApplication.MainWindow != null)
            {
                WinoApplication.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    ApplyBackdrop(value);
                });
            }
        }
    }

    public bool IsCustomTheme
    {
        get
        {
            // If no theme is set, it's not a custom theme
            if (currentApplicationThemeId == null) return false;

            // Check if current theme is not in predefined themes (all themes now are custom or predefined, no system themes)
            return !preDefinedThemes.Exists(a => a.Id == currentApplicationThemeId);
        }
    }

    public FrameworkElement GetShellRootContent() => (WinoApplication.MainWindow as IWinoShellWindow)?.GetRootContent() ?? throw new Exception("No root content found");

    private bool isInitialized = false;

    public async Task InitializeAsync()
    {
        // Already initialized. There is no need.
        if (isInitialized) return;

        RootTheme = _configurationService.Get(UnderlyingThemeService.SelectedAppThemeKey, ApplicationElementTheme.Default);
        AccentColor = _configurationService.Get(AccentColorKey, string.Empty);

        // Set the current theme id. Don't set a default for backward compatibility.
        var storedThemeId = _configurationService.Get<Guid?>(CurrentApplicationThemeKey, null);
        currentApplicationThemeId = storedThemeId;

        // Load backdrop setting, default to Mica
        currentBackdropType = (WindowBackdropType)_configurationService.Get(WindowBackdropTypeKey, (int)WindowBackdropType.Mica);

        // Apply backdrop first, then theme
        ApplyBackdrop(currentBackdropType);
        await ApplyCustomThemeAsync(true);

        // Registering to color changes, thus we notice when user changes theme system wide

        // TODO: WinUI: This event seems to be very unreliable. It causes a crash when the function runs under.
        //uiSettings.ColorValuesChanged -= UISettingsColorChanged;
        //uiSettings.ColorValuesChanged += UISettingsColorChanged;

        isInitialized = true;
    }

    public void ApplyBackdrop(WindowBackdropType backdropType)
    {
        if (WinoApplication.MainWindow is not WindowEx windowEx)
        {
            Debug.WriteLine("MainWindow is not WindowEx, cannot apply backdrop");
            return;
        }

        try
        {
            Microsoft.UI.Xaml.Media.SystemBackdrop backdrop = backdropType switch
            {
                WindowBackdropType.Mica => new MicaBackdrop() { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base },
                WindowBackdropType.MicaAlt => new MicaBackdrop() { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt },
                WindowBackdropType.DesktopAcrylic => new DesktopAcrylicBackdrop(),
                WindowBackdropType.AcrylicBase => new DesktopAcrylicBackdrop(), // Using DesktopAcrylic as base
                WindowBackdropType.AcrylicThin => new DesktopAcrylicBackdrop(), // Using DesktopAcrylic as thin
                WindowBackdropType.None => null,
                _ => new MicaBackdrop() { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base }
            };

            if (windowEx.SystemBackdrop != backdrop)
            {
                windowEx.SystemBackdrop = backdrop;

                BackdropChanged?.Invoke(this, backdropType);

                Debug.WriteLine($"Applied backdrop: {backdropType}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to apply backdrop {backdropType}: {ex.Message}");
        }
    }

    public async Task SetAccentColorAsync(string hexColor, bool preserveTheme = true)
    {
        if (string.IsNullOrEmpty(hexColor))
        {
            // Reset to system accent color
            hexColor = GetSystemAccentColorHex();
        }

        if (preserveTheme)
        {
            // Just update accent color without changing theme
            AccentColor = hexColor;
        }
        else
        {
            // This might trigger theme changes
            AccentColor = hexColor;
            await ApplyCustomThemeAsync(false);
        }
    }

    private void NotifyThemeUpdate()
    {
        if (GetShellRootContent() is not UIElement rootContent) return;

        _ = rootContent.DispatcherQueue.EnqueueAsync(() =>
        {
            ElementThemeChanged?.Invoke(this, RootTheme);
            WeakReferenceMessenger.Default.Send(new ApplicationThemeChanged(_underlyingThemeService.IsUnderlyingThemeDark()));
        }, Microsoft.UI.Dispatching.DispatcherQueuePriority.High);
    }

    private void UISettingsColorChanged(UISettings sender, object args)
    {
        NotifyThemeUpdate();
    }

    public void UpdateSystemCaptionButtonColors()
    {
        GetShellRootContent().DispatcherQueue.TryEnqueue(() =>
        {
            if (WinoApplication.MainWindow is not WindowEx mainWindow) return;

            var titleBar = mainWindow.AppWindow.TitleBar;
            if (titleBar == null) return;

            // Determine if current theme is dark
            bool isDarkTheme = _underlyingThemeService.IsUnderlyingThemeDark();

            // Set button colors based on theme
            // Normal and inactive backgrounds are transparent, but hover/pressed have subtle backgrounds
            titleBar.ButtonBackgroundColor = Color.FromArgb(0, 0, 0, 0); // Transparent
            titleBar.ButtonInactiveBackgroundColor = Color.FromArgb(0, 0, 0, 0); // Transparent

            if (isDarkTheme)
            {
                // Dark theme: use light text/icons for better contrast
                titleBar.ButtonForegroundColor = Color.FromArgb(255, 255, 255, 255); // White
                titleBar.ButtonInactiveForegroundColor = Color.FromArgb(128, 255, 255, 255); // Semi-transparent white
                titleBar.ButtonHoverForegroundColor = Color.FromArgb(255, 255, 255, 255); // White
                titleBar.ButtonPressedForegroundColor = Color.FromArgb(255, 255, 255, 255); // White

                // Subtle hover and pressed backgrounds for dark theme
                titleBar.ButtonHoverBackgroundColor = Color.FromArgb(20, 255, 255, 255); // Very subtle white overlay
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(40, 255, 255, 255); // Slightly more visible white overlay
            }
            else
            {
                // Light theme: use dark text/icons for better contrast
                titleBar.ButtonForegroundColor = Color.FromArgb(255, 0, 0, 0); // Black
                titleBar.ButtonInactiveForegroundColor = Color.FromArgb(128, 0, 0, 0); // Semi-transparent black
                titleBar.ButtonHoverForegroundColor = Color.FromArgb(255, 0, 0, 0); // Black
                titleBar.ButtonPressedForegroundColor = Color.FromArgb(255, 0, 0, 0); // Black

                // Subtle hover and pressed backgrounds for light theme
                titleBar.ButtonHoverBackgroundColor = Color.FromArgb(20, 0, 0, 0); // Very subtle black overlay
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(40, 0, 0, 0); // Slightly more visible black overlay
            }

            Debug.WriteLine($"Updated title bar button colors for {(isDarkTheme ? "dark" : "light")} theme");
        });
    }

    public void UpdateAccentColor(string hex)
    {
        // Change accent color if specified.
        if (!string.IsNullOrEmpty(hex))
        {
            var color = CommunityToolkit.WinUI.Helpers.ColorHelper.ToColor(hex);
            var brush = new SolidColorBrush(color);

            if (_applicationResourceManager.ContainsResourceKey("SystemAccentColor"))
                _applicationResourceManager.ReplaceResource("SystemAccentColor", color);

            if (_applicationResourceManager.ContainsResourceKey("NavigationViewSelectionIndicatorForeground"))
                _applicationResourceManager.ReplaceResource("NavigationViewSelectionIndicatorForeground", brush);

            if (_applicationResourceManager.ContainsResourceKey("SystemControlBackgroundAccentBrush"))
                _applicationResourceManager.ReplaceResource("SystemControlBackgroundAccentBrush", brush);

            if (_applicationResourceManager.ContainsResourceKey("SystemColorControlAccentBrush"))
                _applicationResourceManager.ReplaceResource("SystemColorControlAccentBrush", brush);

            RefreshThemeResource();
        }
    }

    private void RefreshThemeResource()
    {
        var mainApplicationFrame = GetShellRootContent();

        if (mainApplicationFrame == null) return;

        if (mainApplicationFrame.RequestedTheme == ElementTheme.Dark)
        {
            mainApplicationFrame.RequestedTheme = ElementTheme.Light;
            mainApplicationFrame.RequestedTheme = ElementTheme.Dark;
        }
        else if (mainApplicationFrame.RequestedTheme == ElementTheme.Light)
        {
            mainApplicationFrame.RequestedTheme = ElementTheme.Dark;
            mainApplicationFrame.RequestedTheme = ElementTheme.Light;
        }
        else
        {
            var isUnderlyingDark = _underlyingThemeService.IsUnderlyingThemeDark();

            mainApplicationFrame.RequestedTheme = isUnderlyingDark ? ElementTheme.Light : ElementTheme.Dark;
            mainApplicationFrame.RequestedTheme = ElementTheme.Default;
        }
    }

    public async Task ApplyCustomThemeAsync(bool isInitializing)
    {
        // If no theme ID is set, don't apply any theme (for backward compatibility)
        if (currentApplicationThemeId == null)
        {
            Debug.WriteLine("No theme ID set, skipping theme application");
            return;
        }

        AppThemeBase applyingTheme = null;

        var controlThemeList = new List<AppThemeBase>(preDefinedThemes);

        // Don't search for custom themes if applying theme is already in pre-defined templates.
        // This is important for startup performance because we won't be loading the custom themes on launch.

        bool isApplyingPreDefinedTheme = preDefinedThemes.Exists(a => a.Id == currentApplicationThemeId);

        if (isApplyingPreDefinedTheme)
        {
            applyingTheme = preDefinedThemes.Find(a => a.Id == currentApplicationThemeId);
        }
        else
        {
            // User applied custom theme. Load custom themes and find it there.

            var customThemes = await GetCurrentCustomThemesAsync();

            controlThemeList.AddRange(customThemes.Select(a => new CustomAppTheme(a)));

            applyingTheme = controlThemeList.Find(a => a.Id == currentApplicationThemeId);

            // If theme ID is not found in available themes, don't apply any theme (backward compatibility)
            if (applyingTheme == null)
            {
                Debug.WriteLine($"Theme with ID {currentApplicationThemeId} not found, skipping theme application");
                return;
            }
        }

        try
        {
            var existingThemeDictionary = _applicationResourceManager.GetLastResource();

            if (existingThemeDictionary != null && existingThemeDictionary.TryGetValue("ThemeName", out object themeNameString))
            {
                var themeName = themeNameString.ToString();

                // Applying different theme.
                if (themeName != applyingTheme.ThemeName)
                {
                    var resourceDictionaryContent = await applyingTheme.GetThemeResourceDictionaryContentAsync();

                    var resourceDictionary = XamlReader.Load(resourceDictionaryContent) as ResourceDictionary;

                    // Custom themes require special attention for background image because 
                    // they share the same base theme resource dictionary.

                    if (applyingTheme is CustomAppTheme)
                    {
                        resourceDictionary["ThemeBackgroundImage"] = $"ms-appdata:///local/{CustomThemeFolderName}/{applyingTheme.Id}.jpg";
                    }

                    _applicationResourceManager.RemoveResource(existingThemeDictionary);
                    _applicationResourceManager.AddResource(resourceDictionary);

                    bool isSystemTheme = applyingTheme is SystemAppTheme || applyingTheme is CustomAppTheme;

                    if (isSystemTheme)
                    {
                        // For system themes, set the RootElement theme from saved values.
                        // Potential bug: When we set it to system default, theme is not applied when system and
                        // app element theme is different :)

                        var savedElement = _configurationService.Get(UnderlyingThemeService.SelectedAppThemeKey, ApplicationElementTheme.Default);
                        RootTheme = savedElement;

                        // Quickly switch theme to apply theme resource changes.
                        RefreshThemeResource();
                    }
                    else
                        RootTheme = applyingTheme.ForceElementTheme;

                    // Theme has accent color. Override.
                    if (!isInitializing)
                    {
                        AccentColor = applyingTheme.AccentColor;
                    }
                }
                else
                    UpdateSystemCaptionButtonColors();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Apply theme failed -> {ex.Message}");
        }
    }

    public async Task<List<AppThemeBase>> GetAvailableThemesAsync()
    {
        var availableThemes = new List<AppThemeBase>(preDefinedThemes);

        var customThemes = await GetCurrentCustomThemesAsync();

        availableThemes.AddRange(customThemes.Select(a => new CustomAppTheme(a)));

        return availableThemes;
    }

    public async Task<CustomThemeMetadata> CreateNewCustomThemeAsync(string themeName, string accentColor, byte[] wallpaperData)
    {
        if (wallpaperData == null || wallpaperData.Length == 0)
            throw new CustomThemeCreationFailedException(Translator.Exception_CustomThemeMissingWallpaper);

        if (string.IsNullOrEmpty(themeName))
            throw new CustomThemeCreationFailedException(Translator.Exception_CustomThemeMissingName);

        var themes = await GetCurrentCustomThemesAsync();

        if (themes.Exists(a => a.Name == themeName))
            throw new CustomThemeCreationFailedException(Translator.Exception_CustomThemeExists);

        var newTheme = new CustomThemeMetadata()
        {
            Id = Guid.NewGuid(),
            Name = themeName,
            AccentColorHex = accentColor
        };

        // Save wallpaper.
        // Filename would be the same as metadata id, in jpg format.

        var themeFolder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(CustomThemeFolderName, CreationCollisionOption.OpenIfExists);

        var wallpaperFile = await themeFolder.CreateFileAsync($"{newTheme.Id}.jpg", CreationCollisionOption.ReplaceExisting);
        await FileIO.WriteBytesAsync(wallpaperFile, wallpaperData);

        // Generate thumbnail for settings page.

        var thumbnail = await wallpaperFile.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.PicturesView);
        var thumbnailFile = await themeFolder.CreateFileAsync($"{newTheme.Id}_preview.jpg", CreationCollisionOption.ReplaceExisting);

        using (var readerStream = thumbnail.AsStreamForRead())
        {
            byte[] bytes = new byte[readerStream.Length];

            await readerStream.ReadExactlyAsync(bytes);

            var buffer = bytes.AsBuffer();

            await FileIO.WriteBufferAsync(thumbnailFile, buffer);
        }

        // Save metadata.
        var metadataFile = await themeFolder.CreateFileAsync($"{newTheme.Id}.json", CreationCollisionOption.ReplaceExisting);

        var serialized = JsonSerializer.Serialize(newTheme, DomainModelsJsonContext.Default.CustomThemeMetadata);
        await FileIO.WriteTextAsync(metadataFile, serialized);

        return newTheme;
    }

    public async Task<List<CustomThemeMetadata>> GetCurrentCustomThemesAsync()
    {
        var results = new List<CustomThemeMetadata>();

        var themeFolder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(CustomThemeFolderName, CreationCollisionOption.OpenIfExists);

        var allFiles = await themeFolder.GetFilesAsync();

        var themeMetadatas = allFiles.Where(a => a.FileType == ".json");

        foreach (var theme in themeMetadatas)
        {
            var metadata = await GetCustomMetadataAsync(theme).ConfigureAwait(false);

            if (metadata == null) continue;

            results.Add(metadata);
        }

        return results;
    }

    private async Task<CustomThemeMetadata> GetCustomMetadataAsync(IStorageFile file)
    {
        var fileContent = await FileIO.ReadTextAsync(file);

        return JsonSerializer.Deserialize(fileContent, DomainModelsJsonContext.Default.CustomThemeMetadata);
    }

    public string GetSystemAccentColorHex()
        => uiSettings.GetColorValue(UIColorType.Accent).ToHex();

    public List<string> GetAvailableAccountColors()
    {
        return new List<string>()
        {
            "#e74c3c",
            "#c0392b",
            "#e53935",
            "#d81b60",
            
            // Pinks
            "#e91e63",
            "#ec407a",
            "#ff4081",

            // Purples
            "#9b59b6",
            "#8e44ad",
            "#673ab7",

            // Blues
            "#3498db",
            "#2980b9",
            "#2196f3",
            "#03a9f4",
            "#00bcd4",

            // Teals
            "#009688",
            "#1abc9c",
            "#16a085",

            // Greens
            "#2ecc71",
            "#27ae60",
            "#4caf50",
            "#8bc34a",

            // Yellows & Oranges
            "#f1c40f",
            "#f39c12",
            "#ff9800",
            "#ff5722",

            // Browns
            "#795548",
            "#a0522d",

            // Grays
            "#9e9e9e",
            "#607d8b",
            "#34495e",
            "#2c3e50",
        };
    }

    public List<BackdropTypeWrapper> GetAvailableBackdropTypes()
    {
        return new List<BackdropTypeWrapper>
        {
            new BackdropTypeWrapper(WindowBackdropType.None, "None"),
            new BackdropTypeWrapper(WindowBackdropType.Mica, "Mica"),
            new BackdropTypeWrapper(WindowBackdropType.MicaAlt, "Mica Alt"),
            new BackdropTypeWrapper(WindowBackdropType.DesktopAcrylic, "Desktop Acrylic"),
            new BackdropTypeWrapper(WindowBackdropType.AcrylicBase, "Acrylic Base"),
            new BackdropTypeWrapper(WindowBackdropType.AcrylicThin, "Acrylic Thin")
        };
    }
}
