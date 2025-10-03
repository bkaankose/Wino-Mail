# NewThemeService Migration Guide

## Overview

This guide helps you migrate from the original `ThemeService` to the new `NewThemeService` with enhanced backdrop management and improved accent color handling.

## Quick Migration Steps

### 1. Update Service Usage

**Before (Old ThemeService):**
```csharp
var themeService = Services.GetService<IThemeService>();
themeService.RootTheme = ApplicationElementTheme.Dark;
themeService.AccentColor = "#FF5722";
```

**After (NewThemeService):**
```csharp
var newThemeService = Services.GetService<INewThemeService>();
newThemeService.RootTheme = ApplicationElementTheme.Dark;
await newThemeService.SetAccentColorAsync("#FF5722", preserveTheme: true);

// Plus new backdrop management
await newThemeService.ApplyBackdropAsync(WindowBackdropType.Mica);
```

### 2. Remove Hardcoded Backdrops

**Before:**
```xaml
<winuiex:WindowEx.SystemBackdrop>
    <MicaBackdrop />
</winuiex:WindowEx.SystemBackdrop>
```

**After:**
```xaml
<!--  SystemBackdrop will be set by NewThemeService  -->
```

### 3. Update App Initialization

**Before:**
```csharp
protected override async void OnLaunched(LaunchActivatedEventArgs args)
{
    MainWindow = new ShellWindow();
    await InitializeServicesAsync();
    MainWindow.Activate();
}
```

**After:**
```csharp
protected override async void OnLaunched(LaunchActivatedEventArgs args)
{
    // Load backdrop settings before creating window
    var newThemeService = Services.GetService<INewThemeService>();
    var configService = Services.GetService<IConfigurationService>();
    var savedBackdropType = (WindowBackdropType)configService.Get("WindowBackdropTypeKey", (int)WindowBackdropType.Mica);

    MainWindow = new ShellWindow();
    
    // Apply backdrop immediately after window creation
    if (newThemeService != null)
    {
        await newThemeService.ApplyBackdropAsync(savedBackdropType);
    }
    
    await InitializeServicesAsync();
    MainWindow.Activate();
}
```

### 4. Update WinoApplication Services

**Before:**
```csharp
public IEnumerable<IInitializeAsync> GetActivationServices()
{
    yield return DatabaseService;
    yield return TranslationService;
    yield return ThemeService;  // Old service
}
```

**After:**
```csharp
public IEnumerable<IInitializeAsync> GetActivationServices()
{
    yield return DatabaseService;
    yield return TranslationService;
    yield return NewThemeService;  // New service
    // yield return ThemeService;  // Keep for backward compatibility but don't initialize
}
```

## New Features Available After Migration

### 1. Backdrop Management
```csharp
// All available backdrop types
await newThemeService.ApplyBackdropAsync(WindowBackdropType.Mica);
await newThemeService.ApplyBackdropAsync(WindowBackdropType.MicaAlt);
await newThemeService.ApplyBackdropAsync(WindowBackdropType.DesktopAcrylic);
await newThemeService.ApplyBackdropAsync(WindowBackdropType.AcrylicBase);
await newThemeService.ApplyBackdropAsync(WindowBackdropType.AcrylicThin);
await newThemeService.ApplyBackdropAsync(WindowBackdropType.None);

// Persistent backdrop setting
newThemeService.CurrentBackdropType = WindowBackdropType.MicaAlt;
```

### 2. Enhanced Accent Color Management
```csharp
// Set accent color without changing theme
await newThemeService.SetAccentColorAsync("#FF5722", preserveTheme: true);

// Get system accent color
var systemColor = newThemeService.GetSystemAccentColorHex();
await newThemeService.SetAccentColorAsync(systemColor);
```

### 3. Event Handling
```csharp
// New backdrop change events
newThemeService.BackdropChanged += (sender, backdropType) => {
    // Handle backdrop changes
    UpdateUI(backdropType);
};

// Existing events still work
newThemeService.ElementThemeChanged += (sender, theme) => {
    // Handle theme changes
};

newThemeService.AccentColorChanged += (sender, color) => {
    // Handle accent color changes
};
```

## Backward Compatibility

### Both Services Available
- `IThemeService` (original) - still registered and functional
- `INewThemeService` (new) - enhanced version with additional features

### Choosing Which to Use
```csharp
// For new code - use NewThemeService
var newThemeService = Services.GetService<INewThemeService>();

// For existing code that needs compatibility - keep using ThemeService
var oldThemeService = Services.GetService<IThemeService>();
```

### Gradual Migration Strategy
1. **Phase 1**: Register both services, initialize NewThemeService
2. **Phase 2**: Update new features to use NewThemeService
3. **Phase 3**: Migrate existing code gradually
4. **Phase 4**: Eventually phase out old ThemeService (optional)

## Common Migration Issues

### Issue 1: Window Backdrop Not Applied
**Problem**: Backdrop doesn't appear after migration
**Solution**: Ensure backdrop is applied after window creation but before activation

```csharp
MainWindow = new ShellWindow();
await newThemeService.ApplyBackdropAsync(WindowBackdropType.Mica); // Add this
await InitializeServicesAsync();
MainWindow.Activate();
```

### Issue 2: Accent Color Changes Don't Persist
**Problem**: Accent color resets after app restart
**Solution**: Use the enhanced SetAccentColorAsync method

```csharp
// Old way - might not persist properly
newThemeService.AccentColor = "#FF5722";

// New way - properly persisted
await newThemeService.SetAccentColorAsync("#FF5722", preserveTheme: true);
```

### Issue 3: Multiple Service Initialization
**Problem**: Both services being initialized causing conflicts
**Solution**: Only initialize NewThemeService in GetActivationServices()

```csharp
public IEnumerable<IInitializeAsync> GetActivationServices()
{
    yield return DatabaseService;
    yield return TranslationService;
    yield return NewThemeService;  // Only this one
    // Don't yield ThemeService here
}
```

## Testing Your Migration

### 1. Backdrop Functionality
- [ ] App starts with saved backdrop type
- [ ] Backdrop changes are applied immediately
- [ ] Backdrop changes persist after app restart
- [ ] All backdrop types work correctly

### 2. Theme Functionality  
- [ ] Light/Dark theme changes work
- [ ] Custom themes still function
- [ ] Theme changes persist after restart

### 3. Accent Color Management
- [ ] Custom accent colors apply correctly
- [ ] System accent color detection works
- [ ] Accent color changes persist
- [ ] Theme preservation works with accent changes

### 4. Backward Compatibility
- [ ] Existing custom themes still work
- [ ] Old theme-related code continues to function
- [ ] No regression in existing functionality

## Performance Considerations

### Initialization Order
- NewThemeService initializes backdrop settings from saved configuration
- Window creation happens before service initialization for best performance
- Backdrop is applied immediately after window creation

### Runtime Performance
- Backdrop changes are async operations
- Don't change backdrop frequently (e.g., during animations)
- Cache backdrop type to avoid unnecessary changes

## Complete Migration Checklist

- [ ] Update DI container registration
- [ ] Update WinoApplication service initialization
- [ ] Remove hardcoded SystemBackdrop from XAML
- [ ] Update app launch sequence
- [ ] Update settings/preferences UI for backdrop options
- [ ] Test all backdrop types
- [ ] Test theme and accent color functionality
- [ ] Verify persistence across app restarts
- [ ] Update documentation and comments
- [ ] Train team on new features

## Need Help?

If you encounter issues during migration:
1. Check the complete example in `NewThemeServiceExampleViewModel`
2. Review the full documentation in `README_NewThemeService.md`
3. Ensure proper initialization order in your app
4. Verify all required using statements are included