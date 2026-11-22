using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Models.Personalization;
using Wino.Platform.Linux;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Custom themes (the Android and macOS service's members): save with a 480 px preview JPEG, delete, and the editor's
/// unsaved preview, which <see cref="Apply"/> shows instead of the theme in use until it is restored.
/// </summary>
internal sealed partial class GtkThemeService
{
    private sealed record PreviewState(CustomThemeMetadata Metadata, string? WallpaperPath, ApplicationElementTheme ElementTheme);

    private volatile PreviewState? _preview;
    private string? _previewFile;
    private int _previewCounter;
    private bool _previewFolderCleaned;

    public async Task<CustomThemeMetadata?> GetCustomThemeAsync(Guid themeId)
        => (await RefreshCustomThemesAsync()).FirstOrDefault(theme => theme.Id == themeId);

    public Task<List<CustomThemeMetadata>> GetCurrentCustomThemesAsync() => RefreshCustomThemesAsync();

    public async Task<CustomThemeMetadata> SaveCustomThemeAsync(CustomThemeSaveRequest request)
    {
        var themes = await RefreshCustomThemesAsync();
        var validationError = CustomThemeSaveValidator.Validate(request, themes);
        if (validationError != CustomThemeValidationError.None)
        {
            throw new CustomThemeCreationFailedException(validationError switch
            {
                CustomThemeValidationError.MissingName => Translator.Exception_CustomThemeMissingName,
                CustomThemeValidationError.MissingWallpaper => Translator.Exception_CustomThemeMissingWallpaper,
                CustomThemeValidationError.DuplicateName => Translator.Exception_CustomThemeExists,
                CustomThemeValidationError.MissingTheme => Translator.SettingsCustomTheme_DeleteMissing,
                CustomThemeValidationError.InvalidAccent => Translator.ApplicationThemeEditor_InvalidAccent,
                _ => Translator.ApplicationThemeEditor_InvalidSurface
            });
        }

        var accentColor = string.Empty;
        if (!string.IsNullOrWhiteSpace(request.AccentColorHex))
            ThemeColorValidator.TryNormalizeOpaque(request.AccentColorHex, out accentColor);

        var savedTheme = new CustomThemeMetadata
        {
            Id = request.ThemeId ?? Guid.NewGuid(),
            Name = request.Name.Trim(),
            AccentColorHex = accentColor,
            LightPalette = request.LightPalette,
            DarkPalette = request.DarkPalette,
            WallpaperFit = request.WallpaperFit,
            WallpaperAlignment = request.WallpaperFit == ThemeWallpaperFit.Fit ? ThemeWallpaperAlignment.Center : request.WallpaperAlignment
        };

        // An edit without a new picture passes no bytes and keeps the stored wallpaper and preview.
        var wallpaper = request.WallpaperData is { Length: > 0 } data ? data : null;
        await Task.Run(async () =>
        {
            byte[]? preview = null;
            if (wallpaper is not null)
            {
                preview = ThemeImaging.CreatePreviewJpeg(wallpaper)
                    ?? throw new CustomThemeCreationFailedException(Translator.MacPlatform_ThemeWallpaperInvalid);
            }
            await store.SaveAsync(savedTheme, wallpaper, preview).ConfigureAwait(false);
        });

        await RefreshCustomThemesAsync();
        return savedTheme;
    }

    public async Task<bool> DeleteCustomThemeAsync(Guid themeId)
    {
        if (await store.GetAsync(themeId) is null) return false;
        if (_themeId == themeId) await SelectThemeAsync(WinoThemeCatalog.DefaultThemeId, forceReapply: true);
        try
        {
            return await Task.Run(() => store.DeleteAsync(themeId));
        }
        finally
        {
            await RefreshCustomThemesAsync();
        }
    }

    /// <summary>
    /// Shows a theme being edited without saving it. The editor sends picked wallpaper bytes once; they go to a new file
    /// in the cache each time, because GTK keeps the image of a URL it has loaded.
    /// </summary>
    public async Task PreviewCustomThemeAsync(CustomThemeMetadata metadata, byte[]? wallpaperData, ApplicationElementTheme elementTheme)
    {
        string? path;
        if (wallpaperData is { Length: > 0 })
        {
            path = await Task.Run(() => WritePreviewFile(wallpaperData));
        }
        else
        {
            lock (_sync) path = metadata.Id == Guid.Empty ? _previewFile : null;
        }
        _preview = new PreviewState(metadata, path, elementTheme);
        await ApplyThemeToActiveWindowAsync();
    }

    private string? PreviewWallpaperPath(PreviewState preview)
    {
        if (preview.WallpaperPath is { } path && File.Exists(path)) return path;
        if (preview.Metadata.Id == Guid.Empty) return null;
        var stored = store.WallpaperPath(preview.Metadata.Id);
        return File.Exists(stored) ? stored : null;
    }

    private string? WritePreviewFile(byte[] data)
    {
        try
        {
            var folder = Path.Combine(LinuxPaths.FromEnvironment().TemporaryRoot, "theme-preview");
            string? previous;
            lock (_sync)
            {
                if (!_previewFolderCleaned && Directory.Exists(folder)) Directory.Delete(folder, true);
                _previewFolderCleaned = true;
                previous = _previewFile;
            }
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{Interlocked.Increment(ref _previewCounter)}.img");
            File.WriteAllBytes(path, data);
            lock (_sync) _previewFile = path;
            // Only after the new file exists, so the stylesheet never points at a missing picture.
            if (previous is not null) TryDelete(previous);
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(exception, "The theme preview wallpaper could not be written.");
            return null;
        }
    }

    private void ClearPreview()
    {
        _preview = null;
        string? file;
        lock (_sync)
        {
            file = _previewFile;
            _previewFile = null;
        }
        if (file is not null) TryDelete(file);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private sealed class CustomTheme : AppThemeBase
    {
        private readonly string _previewPath;

        public CustomTheme(CustomThemeMetadata metadata, string previewPath) : base(metadata.Name, metadata.Id)
        {
            _previewPath = previewPath;
            AccentColor = metadata.AccentColorHex;
            ForceElementTheme = ApplicationElementTheme.Default;
            Compatibility = ThemeCompatibility.Both;
        }

        public override AppThemeType AppThemeType => AppThemeType.Custom;
        public override Task<string> GetThemeResourceDictionaryContentAsync() => Task.FromResult(string.Empty);
        protected override Task<string> GetPreviewImagePathAsync() => Task.FromResult(File.Exists(_previewPath) ? _previewPath : string.Empty);
    }
}
