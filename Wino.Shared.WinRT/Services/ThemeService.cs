using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Windows.Storage;
using Windows.UI.ViewManagement;
using Wino.Domain;
using Wino.Messaging.Client.Shell;
using Wino.Shared.WinRT.Models.Personalization;
using Wino.Shared.WinRT.Extensions;
using Wino.Domain.Models.Personalization;
using Wino.Domain.Interfaces;
using Wino.Domain.Enums;
using Wino.Domain.Exceptions;






#if NET8_0
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.WinUI.Helpers;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI;
#else
using Windows.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Toolkit.Uwp.Helpers;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

#endif
namespace Wino.Shared.WinRT.Services
{
    /// <summary>
    /// Class providing functionality around switching and restoring theme settings
    /// </summary>
    public class ThemeService : IThemeService
    {
        public const string CustomThemeFolderName = "CustomThemes";

        private const string MicaThemeId = "a160b1b0-2ab8-4e97-a803-f4050f036e25";
        private const string AcrylicThemeId = "fc08e58c-36fd-46e2-a562-26cf277f1467";
        private const string CloudsThemeId = "3b621cc2-e270-4a76-8477-737917cccda0";
        private const string ForestThemeId = "8bc89b37-a7c5-4049-86e2-de1ae8858dbd";
        private const string NightyThemeId = "5b65e04e-fd7e-4c2d-8221-068d3e02d23a";
        private const string SnowflakeThemeId = "e143ddde-2e28-4846-9d98-dad63d6505f1";
        private const string GardenThemeId = "698e4466-f88c-4799-9c61-f0ea1308ed49";

        private Frame mainApplicationFrame;

        public event EventHandler<ApplicationElementTheme> ElementThemeChanged;
        public event EventHandler<string> AccentColorChanged;
        public event EventHandler<string> AccentColorChangedBySystem;

        private const string AccentColorKey = nameof(AccentColorKey);
        private const string CurrentApplicationThemeKey = nameof(CurrentApplicationThemeKey);

        // Custom theme
        public const string CustomThemeAccentColorKey = nameof(CustomThemeAccentColorKey);

        // Keep reference so it does not get optimized/garbage collected
        private readonly UISettings uiSettings = new UISettings();

        private readonly IConfigurationService _configurationService;
        private readonly IUnderlyingThemeService _underlyingThemeService;
        private readonly IApplicationResourceManager<ResourceDictionary> _applicationResourceManager;
        private readonly IAppShellService _appShellService;

        private List<AppThemeBase> preDefinedThemes { get; set; } = new List<AppThemeBase>()
        {
            new SystemAppTheme("Mica", Guid.Parse(MicaThemeId)),
            new SystemAppTheme("Acrylic", Guid.Parse(AcrylicThemeId)),
            new PreDefinedAppTheme("Nighty", Guid.Parse(NightyThemeId), "#e1b12c", ApplicationElementTheme.Dark),
            new PreDefinedAppTheme("Forest", Guid.Parse(ForestThemeId), "#16a085", ApplicationElementTheme.Dark),
            new PreDefinedAppTheme("Clouds", Guid.Parse(CloudsThemeId), "#0984e3", ApplicationElementTheme.Light),
            new PreDefinedAppTheme("Snowflake", Guid.Parse(SnowflakeThemeId), "#4a69bd", ApplicationElementTheme.Light),
            new PreDefinedAppTheme("Garden", Guid.Parse(GardenThemeId), "#05c46b", ApplicationElementTheme.Light),
        };

        public ThemeService(IConfigurationService configurationService,
                            IUnderlyingThemeService underlyingThemeService,
                            IApplicationResourceManager<ResourceDictionary> applicationResourceManager,
                            IAppShellService appShellService)
        {
            _configurationService = configurationService;
            _underlyingThemeService = underlyingThemeService;
            _applicationResourceManager = applicationResourceManager;
            _appShellService = appShellService;
        }

        /// <summary>
        /// Gets or sets (with LocalSettings persistence) the RequestedTheme of the root element.
        /// </summary>
        public ApplicationElementTheme RootTheme
        {
            get => (mainApplicationFrame?.RequestedTheme.ToWinoElementTheme()) ?? ApplicationElementTheme.Default;
            set
            {
                if (mainApplicationFrame == null)
                    return;

                mainApplicationFrame.RequestedTheme = value.ToWindowsElementTheme();

                _configurationService.Set(UnderlyingThemeService.SelectedAppThemeKey, value);

                UpdateSystemCaptionButtonColors();

                // PopupRoot usually needs to react to changes.
                NotifyThemeUpdate();
            }
        }

        private Guid currentApplicationThemeId;

        public Guid CurrentApplicationThemeId
        {
            get => currentApplicationThemeId;
            set
            {
                currentApplicationThemeId = value;

                _configurationService.Set(CurrentApplicationThemeKey, value);

#if NET8_0
                _ = mainApplicationFrame.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High,
                    async () => await ApplyCustomThemeAsync(false));
#else
                _ = mainApplicationFrame.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.High, async () =>
                {
                    await ApplyCustomThemeAsync(false);
                });
#endif
            }
        }


        private string accentColor;

        public string AccentColor
        {
            get => accentColor;
            set
            {
                accentColor = value;

                UpdateAccentColor(value);

                _configurationService.Set(AccentColorKey, value);
                AccentColorChanged?.Invoke(this, value);
            }
        }

        public async Task InitializeAsync()
        {
            // Already initialized. There is no need.
            if (mainApplicationFrame != null)
                return;

            mainApplicationFrame = _appShellService.AppWindow.Content as Frame;

            if (mainApplicationFrame == null) return;

            RootTheme = _configurationService.Get(UnderlyingThemeService.SelectedAppThemeKey, ApplicationElementTheme.Default);
            AccentColor = _configurationService.Get(AccentColorKey, string.Empty);

            // Set the current theme id. Default to Mica.
            var applicationThemeGuid = _configurationService.Get(CurrentApplicationThemeKey, MicaThemeId);

            currentApplicationThemeId = Guid.Parse(applicationThemeGuid);

            await ApplyCustomThemeAsync(true);

            // Registering to color changes, thus we notice when user changes theme system wide
            uiSettings.ColorValuesChanged -= UISettingsColorChanged;
            uiSettings.ColorValuesChanged += UISettingsColorChanged;
        }

        private void NotifyThemeUpdate()
        {
            if (mainApplicationFrame == null) return;

#if NET8_0
            _ = mainApplicationFrame.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High,
                () =>
                {
                    ElementThemeChanged?.Invoke(this, RootTheme);
                    WeakReferenceMessenger.Default.Send(new ApplicationThemeChanged(_underlyingThemeService.IsUnderlyingThemeDark()));
                });
#else
            _ = mainApplicationFrame.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.High, () =>
            {
                ElementThemeChanged?.Invoke(this, RootTheme);
                WeakReferenceMessenger.Default.Send(new ApplicationThemeChanged(_underlyingThemeService.IsUnderlyingThemeDark()));
            });
#endif
        }

        private void UISettingsColorChanged(UISettings sender, object args)
        {
            // Make sure we have a reference to our window so we dispatch a UI change
            if (mainApplicationFrame != null)
            {
                // Dispatch on UI thread so that we have a current appbar to access and change
#if NET8_0
                _ = mainApplicationFrame.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High,
                    () =>
                    {
                        UpdateSystemCaptionButtonColors();
                        var accentColor = sender.GetColorValue(UIColorType.Accent);
                        //AccentColorChangedBySystem?.Invoke(this, accentColor.ToHex());
                    });
#else
                _ = mainApplicationFrame.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.High, () =>
                {
                    UpdateSystemCaptionButtonColors();
                    var accentColor = sender.GetColorValue(UIColorType.Accent);
                    //AccentColorChangedBySystem?.Invoke(this, accentColor.ToHex());
                });
#endif
            }

            NotifyThemeUpdate();
        }

        public void UpdateSystemCaptionButtonColors()
        {
            if (mainApplicationFrame == null) return;

#if NET8_0
            _ = mainApplicationFrame.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
                () =>
                {
                    var titleBar = _appShellService.AppWindow.AppWindow.TitleBar;

                    if (titleBar == null) return;

                    if (_underlyingThemeService.IsUnderlyingThemeDark())
                    {
                        titleBar.ButtonForegroundColor = Colors.White;
                    }
                    else
                    {
                        titleBar.ButtonForegroundColor = Colors.Black;
                    }
                });
#else
            _ = mainApplicationFrame.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                ApplicationViewTitleBar titleBar = ApplicationView.GetForCurrentView().TitleBar;

                if (titleBar == null) return;

                if (_underlyingThemeService.IsUnderlyingThemeDark())
                {
                    titleBar.ButtonForegroundColor = Colors.White;
                }
                else
                {
                    titleBar.ButtonForegroundColor = Colors.Black;
                }
            });
#endif
        }

        public void UpdateAccentColor(string hex)
        {
            // Change accent color if specified.
            if (!string.IsNullOrEmpty(hex))
            {
#if NET8_0
                var brush = new SolidColorBrush(hex.ToColor());
#else
                var brush = new SolidColorBrush(Microsoft.Toolkit.Uwp.Helpers.ColorHelper.ToColor(hex));
#endif

                if (_applicationResourceManager.ContainsResourceKey("SystemAccentColor"))
                    _applicationResourceManager.ReplaceResource("SystemAccentColor", brush);

                if (_applicationResourceManager.ContainsResourceKey("NavigationViewSelectionIndicatorForeground"))
                    _applicationResourceManager.ReplaceResource("NavigationViewSelectionIndicatorForeground", brush);

                RefreshThemeResource();
            }
        }

        private void RefreshThemeResource()
        {
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
                // Fallback to Mica if nothing found.

                var customThemes = await GetCurrentCustomThemesAsync();

                controlThemeList.AddRange(customThemes.Select(a => new CustomAppTheme(a)));

                applyingTheme = controlThemeList.Find(a => a.Id == currentApplicationThemeId) ?? preDefinedThemes.First(a => a.Id == Guid.Parse(MicaThemeId));
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

                await readerStream.ReadAsync(bytes, 0, bytes.Length);

                var buffer = bytes.AsBuffer();

                await FileIO.WriteBufferAsync(thumbnailFile, buffer);
            }

            // Save metadata.
            var metadataFile = await themeFolder.CreateFileAsync($"{newTheme.Id}.json", CreationCollisionOption.ReplaceExisting);

            var serialized = JsonSerializer.Serialize(newTheme);
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

        private static async Task<CustomThemeMetadata> GetCustomMetadataAsync(IStorageFile file)
        {
            var fileContent = await FileIO.ReadTextAsync(file);

            return JsonSerializer.Deserialize<CustomThemeMetadata>(fileContent);
        }

        public string GetSystemAccentColorHex()
            => uiSettings.GetColorValue(UIColorType.Accent).ToHex();
    }
}
