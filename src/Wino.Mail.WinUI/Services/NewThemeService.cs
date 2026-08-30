using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI;
using CommunityToolkit.WinUI.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.UI;
using Windows.UI.ViewManagement;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Personalization;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Extensions;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Models.Personalization;
using Wino.Mail.WinUI.Services;
using Wino.Messaging.Client.Shell;
using WinUIEx;

namespace Wino.Services;

/// <summary>
/// Next-generation theme service with enhanced WinUI support including backdrop management
/// </summary>
public class NewThemeService : INewThemeService
{
    public const string CustomThemeFolderName = "CustomThemes";

    private static string _defaultThemeId = "00000000-0000-0000-0000-000000000000";
    private static string _cloudsThemeId = "3b621cc2-e270-4a76-8477-737917cccda0";
    private static string _forestThemeId = "8bc89b37-a7c5-4049-86e2-de1ae8858dbd";
    private static string _nightyThemeId = "5b65e04e-fd7e-4c2d-8221-068d3e02d23a";
    private static string _snowflakeThemeId = "e143ddde-2e28-4846-9d98-dad63d6505f1";
    private static string _gardenThemeId = "698e4466-f88c-4799-9c61-f0ea1308ed49";

    public event EventHandler<ApplicationElementTheme>? ElementThemeChanged;
    public event EventHandler<string>? AccentColorChanged;
    public event EventHandler<WindowBackdropType>? BackdropChanged;

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
    private readonly IWinoWindowManager _windowManager;

    private List<AppThemeBase> preDefinedThemes { get; set; } = new List<AppThemeBase>()
    {
        new SystemAppTheme("Default", Guid.Parse(_defaultThemeId)),
        new PreDefinedAppTheme("Nighty", Guid.Parse(_nightyThemeId), "#e1b12c", ApplicationElementTheme.Dark),
        new PreDefinedAppTheme("Forest", Guid.Parse(_forestThemeId), "#16a085", ApplicationElementTheme.Dark),
        new PreDefinedAppTheme("Clouds", Guid.Parse(_cloudsThemeId), "#0984e3", ApplicationElementTheme.Light),
        new PreDefinedAppTheme("Snowflake", Guid.Parse(_snowflakeThemeId), "#4a69bd", ApplicationElementTheme.Light),
        new PreDefinedAppTheme("Garden", Guid.Parse(_gardenThemeId), "#05c46b", ApplicationElementTheme.Light),
    };

    public NewThemeService(IConfigurationService configurationService,
                          IUnderlyingThemeService underlyingThemeService,
                          IApplicationResourceManager<ResourceDictionary> applicationResourceManager,
                          IWinoWindowManager windowManager)
    {
        _configurationService = configurationService;
        _underlyingThemeService = underlyingThemeService;
        _applicationResourceManager = applicationResourceManager;
        _windowManager = windowManager;
    }

    /// <summary>
    /// Gets or sets (with LocalSettings persistence) the RequestedTheme of the root element.
    /// </summary>
    public ApplicationElementTheme RootTheme
    {
        get
        {
            var rootContent = TryGetShellRootContent();
            if (rootContent == null)
                return _configurationService.Get(UnderlyingThemeService.SelectedAppThemeKey, ApplicationElementTheme.Default);

            return rootContent.RequestedTheme.ToWinoElementTheme();
        }
        set
        {
            var rootContent = TryGetShellRootContent();
            if (rootContent != null)
                rootContent.RequestedTheme = value.ToWindowsElementTheme();

            _configurationService.Set(UnderlyingThemeService.SelectedAppThemeKey, value);

            if (!string.IsNullOrEmpty(accentColor))
                UpdateAccentColor(accentColor);

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

            var window = GetThemeWindow();
            if (window != null)
            {
                window.DispatcherQueue.TryEnqueue(async () =>
                {
                    await ApplyCustomThemeAsync(false);
                });
            }
        }
    }

    private string accentColor = string.Empty;

    public string AccentColor
    {
        get { return accentColor; }
        set
        {
            accentColor = value;

            UpdateAccentColor(string.IsNullOrWhiteSpace(value) ? GetSystemAccentColorHex() : value);

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
            value = NormalizeBackdropType(value);

            // Only update if the backdrop type has actually changed
            if (currentBackdropType == value) return;

            currentBackdropType = value;
            _configurationService.Set(WindowBackdropTypeKey, (int)value);

            var window = GetThemeWindow();
            if (window != null)
            {
                window.DispatcherQueue.TryEnqueue(() =>
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

    public FrameworkElement GetShellRootContent()
    {
        return TryGetShellRootContent() ?? throw new Exception("No root content found");
    }

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

        // Load the backdrop setting, default to Mica. Older releases stored
        // AcrylicBase and AcrylicThin, which WinUI 3 renders identically to DesktopAcrylic.
        var storedBackdropType = (WindowBackdropType)_configurationService.Get(WindowBackdropTypeKey, (int)WindowBackdropType.Mica);
        currentBackdropType = NormalizeBackdropType(storedBackdropType);

        if (storedBackdropType != currentBackdropType)
            _configurationService.Set(WindowBackdropTypeKey, (int)currentBackdropType);

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
        backdropType = NormalizeBackdropType(backdropType);

        if (GetThemeWindow() is not WindowEx windowEx)
        {
            Debug.WriteLine("No active WindowEx found, cannot apply backdrop");
            return;
        }

        try
        {
            Microsoft.UI.Xaml.Media.SystemBackdrop? backdrop = backdropType switch
            {
                WindowBackdropType.Mica => new MicaBackdrop() { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base },
                WindowBackdropType.MicaAlt => new MicaBackdrop() { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt },
                WindowBackdropType.DesktopAcrylic => new DesktopAcrylicBackdrop(),
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
        if (TryGetShellRootContent() is not UIElement rootContent) return;

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
        var rootContent = TryGetShellRootContent();
        if (rootContent == null) return;

        rootContent.DispatcherQueue.TryEnqueue(() =>
        {
            if (GetThemeWindow() is not WindowEx mainWindow) return;

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
            var white = Color.FromArgb(255, 255, 255, 255);
            var black = Color.FromArgb(255, 0, 0, 0);
            var light1 = BlendColor(color, white, 0.20);
            var light2 = BlendColor(color, white, 0.40);
            var light3 = BlendColor(color, white, 0.60);
            var dark1 = BlendColor(color, black, 0.20);
            var dark2 = BlendColor(color, black, 0.40);
            var dark3 = BlendColor(color, black, 0.60);
            var isDarkTheme = _underlyingThemeService.IsUnderlyingThemeDark();

            SetColorResource("SystemAccentColor", color);
            SetColorResource("SystemAccentColorLight1", light1);
            SetColorResource("SystemAccentColorLight2", light2);
            SetColorResource("SystemAccentColorLight3", light3);
            SetColorResource("SystemAccentColorDark1", dark1);
            SetColorResource("SystemAccentColorDark2", dark2);
            SetColorResource("SystemAccentColorDark3", dark3);

            // WinUI control templates redirect their checked/selected states to these
            // semantic brushes. Mutating the existing brushes is important: many of
            // those redirects are StaticResource references and retain the original
            // brush instance for the lifetime of the application.
            var accentFill = isDarkTheme ? light2 : dark1;
            SetBrushResource("AccentFillColorDefaultBrush", accentFill);
            SetBrushResource("AccentFillColorSecondaryBrush", accentFill, 0.90);
            SetBrushResource("AccentFillColorTertiaryBrush", accentFill, 0.80);
            SetBrushResource("AccentFillColorSelectedTextBackgroundBrush", color);

            SetBrushResource("AccentTextFillColorPrimaryBrush", isDarkTheme ? light3 : dark2);
            SetBrushResource("AccentTextFillColorSecondaryBrush", isDarkTheme ? light3 : dark3);
            SetBrushResource("AccentTextFillColorTertiaryBrush", isDarkTheme ? light2 : dark1);

            SetBrushResource("NavigationViewSelectionIndicatorForeground", accentFill);
            SetBrushResource("SystemControlBackgroundAccentBrush", accentFill);
            SetBrushResource("SystemColorControlAccentBrush", accentFill);

            RefreshThemeResource();
        }
    }

    private void SetColorResource(string resourceKey, Color color)
    {
        if (_applicationResourceManager.ContainsResourceKey(resourceKey))
        {
            _applicationResourceManager.ReplaceResource(resourceKey, color);
        }
    }

    private void SetBrushResource(string resourceKey, Color color, double opacity = 1)
    {
        if (!_applicationResourceManager.ContainsResourceKey(resourceKey))
        {
            return;
        }

        var brush = _applicationResourceManager.GetResource<object>(resourceKey) as SolidColorBrush;

        if (brush != null)
        {
            brush.Color = color;
            brush.Opacity = opacity;
        }
        else
        {
            _applicationResourceManager.ReplaceResource(resourceKey, new SolidColorBrush(color)
            {
                Opacity = opacity
            });
        }
    }

    private static Color BlendColor(Color source, Color target, double amount)
    {
        return Color.FromArgb(
            source.A,
            (byte)Math.Round(source.R + ((target.R - source.R) * amount)),
            (byte)Math.Round(source.G + ((target.G - source.G) * amount)),
            (byte)Math.Round(source.B + ((target.B - source.B) * amount)));
    }

    private void RefreshThemeResource()
    {
        var mainApplicationFrame = TryGetShellRootContent();
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

    public Task ApplyCustomThemeAsync(bool isInitializing)
        => ApplyCustomThemeCoreAsync(isInitializing, forceReapply: false, throwOnError: false);

    private async Task ApplyCustomThemeCoreAsync(bool isInitializing, bool forceReapply, bool throwOnError)
    {
        // If no theme ID is set, don't apply any theme (for backward compatibility)
        if (currentApplicationThemeId == null)
        {
            Debug.WriteLine("No theme ID set, skipping theme application");
            return;
        }

        AppThemeBase? applyingTheme = null;

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

        if (applyingTheme == null)
        {
            Debug.WriteLine($"Theme with ID {currentApplicationThemeId} not found, skipping theme application");
            return;
        }

        try
        {
            var existingThemeDictionary = _applicationResourceManager.GetLastResource();

            if (existingThemeDictionary != null && existingThemeDictionary.TryGetValue("ThemeName", out object? themeNameString))
            {
                var themeName = themeNameString?.ToString();

                // Applying different theme.
                if (forceReapply || themeName != applyingTheme.ThemeName || applyingTheme is CustomAppTheme)
                {
                    var resourceDictionaryContent = await applyingTheme.GetThemeResourceDictionaryContentAsync();

                    var resourceDictionary = XamlReader.Load(resourceDictionaryContent) as ResourceDictionary;
                    if (resourceDictionary == null)
                    {
                        return;
                    }

                    // Custom themes require special attention for background image because 
                    // they share the same base theme resource dictionary.

                    if (applyingTheme is CustomAppTheme customTheme)
                    {
                        ConfigureCustomThemeDictionary(
                            resourceDictionary,
                            customTheme.Metadata,
                            $"ms-appdata:///local/{CustomThemeFolderName}/{applyingTheme.Id}.jpg");
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

            if (throwOnError)
                throw;
        }
    }

    private static void ConfigureCustomThemeDictionary(
        ResourceDictionary dictionary,
        CustomThemeMetadata metadata,
        string wallpaperUri)
    {
        dictionary["ThemeBackgroundImage"] = wallpaperUri;

        if (dictionary["WinoApplicationBackgroundColor"] is ImageBrush imageBrush)
        {
            imageBrush.ImageSource = new BitmapImage(new Uri(wallpaperUri));
            imageBrush.Stretch = metadata.WallpaperFit == ThemeWallpaperFit.Fit ? Stretch.Uniform : Stretch.UniformToFill;
            imageBrush.AlignmentX = metadata.WallpaperFit == ThemeWallpaperFit.Fit
                ? AlignmentX.Center
                : metadata.WallpaperAlignment switch
                {
                    ThemeWallpaperAlignment.TopLeft or ThemeWallpaperAlignment.Left or ThemeWallpaperAlignment.BottomLeft => AlignmentX.Left,
                    ThemeWallpaperAlignment.TopRight or ThemeWallpaperAlignment.Right or ThemeWallpaperAlignment.BottomRight => AlignmentX.Right,
                    _ => AlignmentX.Center
                };
            imageBrush.AlignmentY = metadata.WallpaperFit == ThemeWallpaperFit.Fit
                ? AlignmentY.Center
                : metadata.WallpaperAlignment switch
                {
                    ThemeWallpaperAlignment.TopLeft or ThemeWallpaperAlignment.Top or ThemeWallpaperAlignment.TopRight => AlignmentY.Top,
                    ThemeWallpaperAlignment.BottomLeft or ThemeWallpaperAlignment.Bottom or ThemeWallpaperAlignment.BottomRight => AlignmentY.Bottom,
                    _ => AlignmentY.Center
                };
        }

        ApplyPalette(dictionary.ThemeDictionaries["Light"] as ResourceDictionary, metadata.LightPalette?.Resolve(false) ?? CustomThemePalette.CreateDefaults(false));
        ApplyPalette(dictionary.ThemeDictionaries["Dark"] as ResourceDictionary, metadata.DarkPalette?.Resolve(true) ?? CustomThemePalette.CreateDefaults(true));
    }

    private static void ApplyPalette(ResourceDictionary? dictionary, CustomThemePalette palette)
    {
        if (dictionary == null)
            return;

        SetPaletteColor(dictionary, "MainCustomThemeColor", palette.MainCustomThemeColor, asColor: true);
        SetPaletteColor(dictionary, "MailListHeaderBackgroundColor", palette.MailListHeaderBackgroundColor);
        SetPaletteColor(dictionary, "CalendarDefaultHourBackgroundBrush", palette.CalendarDefaultHourBackgroundBrush);
        SetPaletteColor(dictionary, "CalendarHoverHourBackgroundBrush", palette.CalendarHoverHourBackgroundBrush);
        SetPaletteColor(dictionary, "CalendarWorkHourBackgroundBrush", palette.CalendarWorkHourBackgroundBrush);
        SetPaletteColor(dictionary, "CalendarSelectedHourBackgroundBrush", palette.CalendarSelectedHourBackgroundBrush);
        SetPaletteColor(dictionary, "WinoContentZoneBackgroud", palette.WinoContentZoneBackgroud);
        SetPaletteColor(dictionary, "ReadingPaneBackgroundColorBrush", palette.ReadingPaneBackgroundColorBrush);
        SetPaletteColor(dictionary, "NavigationViewContentBackground", palette.NavigationViewContentBackground);
    }

    private static void SetPaletteColor(ResourceDictionary dictionary, string key, string? hex, bool asColor = false)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return;

        var color = ColorHelper.ToColor(hex);

        if (asColor)
        {
            dictionary[key] = color;
        }
        else if (dictionary.TryGetValue(key, out var value) && value is SolidColorBrush brush)
        {
            brush.Color = color;
            brush.Opacity = 1;
        }
        else
        {
            dictionary[key] = new SolidColorBrush(color);
        }
    }

    public async Task<List<AppThemeBase>> GetAvailableThemesAsync()
    {
        var availableThemes = new List<AppThemeBase>(preDefinedThemes);

        var customThemes = await GetCurrentCustomThemesAsync();

        availableThemes.AddRange(customThemes.Select(a => new CustomAppTheme(a)));

        return availableThemes;
    }

    public async Task SelectThemeAsync(Guid themeId, bool forceReapply = false)
    {
        var availableThemes = await GetAvailableThemesAsync();
        if (availableThemes.All(theme => theme.Id != themeId))
            throw new InvalidOperationException($"Theme '{themeId}' is not available.");

        var previousState = CaptureRuntimeState();
        currentApplicationThemeId = themeId;
        _configurationService.Set(CurrentApplicationThemeKey, currentApplicationThemeId);

        try
        {
            await ApplyCustomThemeCoreAsync(isInitializing: false, forceReapply: forceReapply, throwOnError: true);
        }
        catch
        {
            await RestoreRuntimeStateAsync(previousState);
            throw;
        }
    }

    public ThemeRuntimeState CaptureRuntimeState()
        => new(currentApplicationThemeId, currentApplicationThemeId ?? Guid.Parse(_defaultThemeId), AccentColor, RootTheme);

    public async Task PreviewCustomThemeAsync(
        CustomThemeMetadata metadata,
        byte[]? wallpaperData,
        ApplicationElementTheme elementTheme)
    {
        var wallpaperUri = metadata.Id == Guid.Empty
            ? $"ms-appdata:///local/{CustomThemeFolderName}/editor-preview.jpg"
            : $"ms-appdata:///local/{CustomThemeFolderName}/{metadata.Id}.jpg";

        if (wallpaperData is { Length: > 0 })
        {
            var themeFolder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(CustomThemeFolderName, CreationCollisionOption.OpenIfExists);
            const string previewFileName = "editor-preview.jpg";
            await ReplaceBytesAtomicallyAsync(themeFolder, previewFileName, wallpaperData);
            wallpaperUri = $"ms-appdata:///local/{CustomThemeFolderName}/{previewFileName}";
        }

        var customThemeFile = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///AppThemes/Custom.xaml"));
        var content = await FileIO.ReadTextAsync(customThemeFile);
        var dictionary = XamlReader.Load(content) as ResourceDictionary
                         ?? throw new InvalidOperationException("Custom theme resources could not be loaded.");
        ConfigureCustomThemeDictionary(dictionary, metadata, wallpaperUri);

        var existingDictionary = _applicationResourceManager.GetLastResource();
        if (existingDictionary != null)
            _applicationResourceManager.RemoveResource(existingDictionary);

        _applicationResourceManager.AddResource(dictionary);
        RootTheme = elementTheme;
        AccentColor = metadata.AccentColorHex;
        RefreshThemeResource();
    }

    public async Task RestoreRuntimeStateAsync(ThemeRuntimeState state)
    {
        currentApplicationThemeId = state.EffectiveThemeId;
        await ApplyCustomThemeCoreAsync(isInitializing: false, forceReapply: true, throwOnError: true);
        RootTheme = state.ElementTheme;
        AccentColor = state.AccentColor;
        currentApplicationThemeId = state.ThemeId;
        _configurationService.Set(CurrentApplicationThemeKey, currentApplicationThemeId);

        var themeFolder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(CustomThemeFolderName, CreationCollisionOption.OpenIfExists);
        await DeleteThemeAssetIfExistsAsync(themeFolder, "editor-preview.jpg");
    }

    public async Task<CustomThemeMetadata?> GetCustomThemeAsync(Guid themeId)
        => (await GetCurrentCustomThemesAsync()).FirstOrDefault(theme => theme.Id == themeId);

    public async Task<CustomThemeMetadata> SaveCustomThemeAsync(CustomThemeSaveRequest request)
    {
        var themeName = request.Name?.Trim();
        var themes = await GetCurrentCustomThemesAsync();
        var validationError = CustomThemeSaveValidator.Validate(request, themes);
        if (validationError != CustomThemeValidationError.None)
        {
            var message = validationError switch
            {
                CustomThemeValidationError.MissingName => Translator.Exception_CustomThemeMissingName,
                CustomThemeValidationError.MissingWallpaper => Translator.Exception_CustomThemeMissingWallpaper,
                CustomThemeValidationError.DuplicateName => Translator.Exception_CustomThemeExists,
                CustomThemeValidationError.MissingTheme => Translator.SettingsCustomTheme_DeleteMissing,
                CustomThemeValidationError.InvalidAccent => Translator.ApplicationThemeEditor_InvalidAccent,
                _ => Translator.ApplicationThemeEditor_InvalidSurface
            };
            throw new CustomThemeCreationFailedException(message);
        }

        var accentColor = string.Empty;
        if (!string.IsNullOrWhiteSpace(request.AccentColorHex))
            ThemeColorValidator.TryNormalizeOpaque(request.AccentColorHex, out accentColor);

        var savedTheme = new CustomThemeMetadata
        {
            Id = request.ThemeId ?? Guid.NewGuid(),
            Name = themeName!,
            AccentColorHex = accentColor,
            LightPalette = request.LightPalette,
            DarkPalette = request.DarkPalette,
            WallpaperFit = request.WallpaperFit,
            WallpaperAlignment = request.WallpaperFit == ThemeWallpaperFit.Fit
                ? ThemeWallpaperAlignment.Center
                : request.WallpaperAlignment
        };

        var themeFolder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(CustomThemeFolderName, CreationCollisionOption.OpenIfExists);

        if (request.WallpaperData is { Length: > 0 } wallpaperData)
        {
            var wallpaperFile = await ReplaceBytesAtomicallyAsync(themeFolder, $"{savedTheme.Id}.jpg", wallpaperData);
            using var thumbnail = await wallpaperFile.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.PicturesView);
            using var readerStream = thumbnail.AsStreamForRead();
            var bytes = new byte[readerStream.Length];
            await readerStream.ReadExactlyAsync(bytes);
            await ReplaceBytesAtomicallyAsync(themeFolder, $"{savedTheme.Id}_preview.jpg", bytes);
        }

        var serialized = JsonSerializer.Serialize(savedTheme, DomainModelsJsonContext.Default.CustomThemeMetadata);
        await ReplaceTextAtomicallyAsync(themeFolder, $"{savedTheme.Id}.json", serialized);

        return savedTheme;
    }

    private static async Task<StorageFile> ReplaceBytesAtomicallyAsync(StorageFolder folder, string fileName, byte[] data)
    {
        var tempFile = await folder.CreateFileAsync($"{fileName}.{Guid.NewGuid():N}.tmp", CreationCollisionOption.FailIfExists);
        await FileIO.WriteBytesAsync(tempFile, data);
        var existing = await folder.TryGetItemAsync(fileName) as StorageFile;

        if (existing == null)
            await tempFile.RenameAsync(fileName, NameCollisionOption.FailIfExists);
        else
            await tempFile.MoveAndReplaceAsync(existing);

        return await folder.GetFileAsync(fileName);
    }

    private static async Task ReplaceTextAtomicallyAsync(StorageFolder folder, string fileName, string text)
    {
        var tempFile = await folder.CreateFileAsync($"{fileName}.{Guid.NewGuid():N}.tmp", CreationCollisionOption.FailIfExists);
        await FileIO.WriteTextAsync(tempFile, text);
        var existing = await folder.TryGetItemAsync(fileName) as StorageFile;

        if (existing == null)
            await tempFile.RenameAsync(fileName, NameCollisionOption.FailIfExists);
        else
            await tempFile.MoveAndReplaceAsync(existing);
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

    public async Task<bool> DeleteCustomThemeAsync(Guid themeId)
    {
        var themeFolder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(CustomThemeFolderName, CreationCollisionOption.OpenIfExists);
        var metadataFileName = $"{themeId}.json";
        var themeItem = await themeFolder.TryGetItemAsync(metadataFileName);

        if (themeItem == null)
        {
            return false;
        }

        if (currentApplicationThemeId == themeId)
        {
            currentApplicationThemeId = preDefinedThemes[0].Id;
            _configurationService.Set(CurrentApplicationThemeKey, currentApplicationThemeId);
            await ApplyCustomThemeAsync(false);
        }

        await DeleteThemeAssetIfExistsAsync(themeFolder, metadataFileName);
        await DeleteThemeAssetIfExistsAsync(themeFolder, $"{themeId}.jpg");
        await DeleteThemeAssetIfExistsAsync(themeFolder, $"{themeId}_preview.jpg");

        return true;
    }

    private async Task<CustomThemeMetadata?> GetCustomMetadataAsync(IStorageFile file)
    {
        var fileContent = await FileIO.ReadTextAsync(file);

        return JsonSerializer.Deserialize(fileContent, DomainModelsJsonContext.Default.CustomThemeMetadata);
    }

    private static async Task DeleteThemeAssetIfExistsAsync(StorageFolder themeFolder, string fileName)
    {
        var item = await themeFolder.TryGetItemAsync(fileName);

        if (item != null)
        {
            await item.DeleteAsync();
        }
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
            new BackdropTypeWrapper(WindowBackdropType.DesktopAcrylic, "Desktop Acrylic")
        };
    }

    private static WindowBackdropType NormalizeBackdropType(WindowBackdropType backdropType)
    {
#pragma warning disable CS0618 // Legacy values are intentionally supported for settings migration.
        return backdropType is WindowBackdropType.AcrylicBase or WindowBackdropType.AcrylicThin
            ? WindowBackdropType.DesktopAcrylic
            : backdropType;
#pragma warning restore CS0618
    }

    private WindowEx? GetThemeWindow() => _windowManager.ActiveWindow ?? WinoApplication.MainWindow;

    private FrameworkElement? TryGetShellRootContent()
    {
        var window = GetThemeWindow();
        if (window == null)
            return null;

        try
        {
            if (window is IWinoShellWindow shellWindow)
                return shellWindow.GetRootContent();

            return window.Content as FrameworkElement;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Skipping root content lookup for closed window: {ex.Message}");
            return null;
        }
    }

    public async Task ApplyThemeToActiveWindowAsync()
    {
        ApplyBackdrop(currentBackdropType);
        RootTheme = _configurationService.Get(UnderlyingThemeService.SelectedAppThemeKey, ApplicationElementTheme.Default);
        await ApplyCustomThemeAsync(false);
        UpdateSystemCaptionButtonColors();
    }
}
