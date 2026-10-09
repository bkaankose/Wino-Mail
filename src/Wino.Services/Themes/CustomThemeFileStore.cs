#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Personalization;

namespace Wino.Services.Themes;

/// <summary>
/// File storage for custom application themes, in the same layout and format the Windows
/// NewThemeService writes to its LocalFolder: a <c>CustomThemes</c> folder holding <c>{id}.json</c>
/// (<see cref="DomainModelsJsonContext"/> metadata), <c>{id}.jpg</c> (the wallpaper bytes exactly as
/// picked, also for PNG files) and <c>{id}_preview.jpg</c> (a thumbnail). Every write replaces the
/// target through a temporary file, so a crash never leaves half a theme behind.
/// </summary>
public sealed class CustomThemeFileStore
{
    public const string FolderName = "CustomThemes";

    private readonly string _root;

    public CustomThemeFileStore(IApplicationConfiguration configuration)
        : this(Path.Combine(configuration.ApplicationDataFolderPath, FolderName))
    {
    }

    /// <param name="root">The CustomThemes folder itself.</param>
    public CustomThemeFileStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = root;
    }

    public string RootPath => _root;

    public string MetadataPath(Guid themeId) => Path.Combine(_root, $"{themeId}.json");
    public string WallpaperPath(Guid themeId) => Path.Combine(_root, $"{themeId}.jpg");
    public string PreviewPath(Guid themeId) => Path.Combine(_root, $"{themeId}_preview.jpg");

    /// <summary>Every readable theme. Unreadable metadata is skipped and logged rather than failing the gallery.</summary>
    public async Task<List<CustomThemeMetadata>> ListAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<CustomThemeMetadata>();
        if (!Directory.Exists(_root))
            return results;

        foreach (var file in Directory.EnumerateFiles(_root, "*.json"))
        {
            // Pattern matching can be loose on some file systems; temporary files never count.
            if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            var metadata = await ReadAsync(file, cancellationToken).ConfigureAwait(false);
            if (metadata != null)
                results.Add(metadata);
        }

        return results;
    }

    public async Task<CustomThemeMetadata?> GetAsync(Guid themeId, CancellationToken cancellationToken = default)
    {
        var path = MetadataPath(themeId);
        return File.Exists(path) ? await ReadAsync(path, cancellationToken).ConfigureAwait(false) : null;
    }

    /// <summary>
    /// Writes the metadata and, when given, the wallpaper and its preview. The metadata goes last,
    /// so a theme only appears in <see cref="ListAsync"/> once its images are in place.
    /// </summary>
    public async Task SaveAsync(CustomThemeMetadata metadata, byte[]? wallpaper, byte[]? preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        Directory.CreateDirectory(_root);

        if (wallpaper is { Length: > 0 })
            await ReplaceAsync(WallpaperPath(metadata.Id), wallpaper, cancellationToken).ConfigureAwait(false);

        if (preview is { Length: > 0 })
            await ReplaceAsync(PreviewPath(metadata.Id), preview, cancellationToken).ConfigureAwait(false);

        var json = JsonSerializer.SerializeToUtf8Bytes(metadata, DomainModelsJsonContext.Default.CustomThemeMetadata);
        await ReplaceAsync(MetadataPath(metadata.Id), json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes the theme's three files. False when the theme does not exist.</summary>
    public Task<bool> DeleteAsync(Guid themeId)
    {
        var metadataPath = MetadataPath(themeId);
        if (!File.Exists(metadataPath))
            return Task.FromResult(false);

        // Metadata first: once it is gone the theme is no longer listed, whatever happens to the images.
        File.Delete(metadataPath);
        DeleteIfExists(WallpaperPath(themeId));
        DeleteIfExists(PreviewPath(themeId));
        return Task.FromResult(true);
    }

    private static async Task<CustomThemeMetadata?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync(stream, DomainModelsJsonContext.Default.CustomThemeMetadata, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Skipping unreadable custom theme metadata {File}.", Path.GetFileName(path));
            return null;
        }
    }

    private static async Task ReplaceAsync(string path, byte[] data, CancellationToken cancellationToken)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, data, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            DeleteIfExists(temporary);
            throw;
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
