# NewThemeService Documentation

## Overview

The `NewThemeService` is an enhanced theme management service designed specifically for WinUI applications. It extends the capabilities of the original `ThemeService` with advanced features like backdrop management, improved accent color handling, and better window initialization support.

## Key Features

### 🎨 Window Backdrop Management
- **Mica**: Modern translucent material with system-aware tinting
- **MicaAlt**: Alternative Mica variant with different tinting behavior  
- **DesktopAcrylic**: Semi-transparent acrylic background
- **AcrylicBase & AcrylicThin**: Different acrylic variants
- **None**: No backdrop (solid background)

### 🌈 Enhanced Accent Color Management
- Set custom accent colors without changing themes
- Preserve theme while changing accent colors
- Automatic system accent color detection
- Backward-compatible color management

### 🖼️ Custom Wallpaper Support
- Create custom themes with user wallpapers
- Automatic thumbnail generation
- Persistent theme storage
- Reuses existing custom theme functionality

### ⚡ Improved Initialization
- Backdrop settings applied before window creation
- Persistent settings restoration
- Proper service initialization order

## Usage Examples

### Basic Setup

```csharp
// Service is automatically registered in DI container
var newThemeService = WinoApplication.Current.Services.GetService<INewThemeService>();
```

### Changing Window Backdrop

```csharp
// Apply different backdrop types
await newThemeService.ApplyBackdropAsync(WindowBackdropType.Mica);
await newThemeService.ApplyBackdropAsync(WindowBackdropType.DesktopAcrylic);
await newThemeService.ApplyBackdropAsync(WindowBackdropType.None);

// Persist the setting
newThemeService.CurrentBackdropType = WindowBackdropType.MicaAlt;
```

### Custom Accent Color

```csharp
// Set accent color while preserving theme
await newThemeService.SetAccentColorAsync("#FF6B47", preserveTheme: true);

// Reset to system accent color
var systemAccent = newThemeService.GetSystemAccentColorHex();
await newThemeService.SetAccentColorAsync(systemAccent);
```

### Event Handling

```csharp
newThemeService.BackdropChanged += (sender, backdropType) => {
    Debug.WriteLine($"Backdrop changed to: {backdropType}");
};

newThemeService.ElementThemeChanged += (sender, theme) => {
    Debug.WriteLine($"Theme changed to: {theme}");
};

newThemeService.AccentColorChanged += (sender, color) => {
    Debug.WriteLine($"Accent color changed to: {color}");
};
```

### Creating Custom Themes

```csharp
// Create custom theme with wallpaper
var wallpaperData = await GetWallpaperBytesAsync(); // Your implementation
var customTheme = await newThemeService.CreateNewCustomThemeAsync(
    "My Theme", 
    "#FF5722", 
    wallpaperData);

// Apply the custom theme
newThemeService.CurrentApplicationThemeId = customTheme.Id;
```

## Service Registration

The service is automatically registered in the DI container:

```csharp
// In CoreUWPContainerSetup.cs
services.AddSingleton<INewThemeService, NewThemeService>();
```

## Initialization Order

The service is initialized during app startup:

```csharp
// In WinoApplication.cs
public IEnumerable<IInitializeAsync> GetActivationServices()
{
    yield return DatabaseService;
    yield return TranslationService;
    yield return NewThemeService;  // Initializes before window activation
}
```

## Backdrop Application Flow

1. **App Launch**: Saved backdrop type is loaded from configuration
2. **Window Creation**: Window is created without hardcoded backdrop
3. **Service Initialization**: NewThemeService initializes and applies saved backdrop
4. **Runtime Changes**: User can change backdrop which is immediately applied and persisted

## Backward Compatibility

- The original `IThemeService` is still registered and functional
- All existing theme functionality is preserved  
- Custom theme creation and management works as before
- Accent color management is enhanced but compatible

## Configuration Keys

The service uses these persistent configuration keys:

- `WindowBackdropTypeKey`: Stores the selected backdrop type (int)
- `AccentColorKey`: Stores custom accent color (string)
- `CurrentApplicationThemeKey`: Stores selected theme ID (Guid)
- `SelectedAppThemeKey`: Stores element theme preference (ApplicationElementTheme)

## Best Practices

### ✅ Do
- Initialize the service early in app lifecycle
- Use async methods for backdrop changes
- Handle backdrop change errors gracefully
- Subscribe to events for UI updates
- Preserve themes when changing accent colors

### ❌ Don't
- Set hardcoded SystemBackdrop in XAML
- Change backdrop on every frame/animation
- Ignore initialization errors
- Forget to unsubscribe from events

## Migration from Old ThemeService

1. **Update DI Registration**: Both services are registered, choose which to use
2. **Update Initialization**: Change `GetActivationServices()` to use `NewThemeService`
3. **Remove Hardcoded Backdrops**: Remove `<MicaBackdrop />` from XAML
4. **Add Backdrop Management**: Use `ApplyBackdropAsync()` for backdrop changes
5. **Enhanced Accent Colors**: Use `SetAccentColorAsync()` for better accent color management

## See Also

- `INewThemeService` interface documentation
- `WindowBackdropType` enumeration
- `NewThemeServiceExampleViewModel` for complete usage example
- Original `ThemeService` for backward compatibility