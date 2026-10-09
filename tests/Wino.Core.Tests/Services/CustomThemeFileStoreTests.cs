using System.IO;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Personalization;
using Wino.Services.Themes;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class CustomThemeFileStoreTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"wino-theme-tests-{Guid.NewGuid():N}");

    private CustomThemeFileStore CreateStore()
    {
        var configuration = new Mock<IApplicationConfiguration>();
        configuration.SetupGet(item => item.ApplicationDataFolderPath).Returns(_dataRoot);
        return new CustomThemeFileStore(configuration.Object);
    }

    private static CustomThemeMetadata CreateMetadata(string name = "Harbour") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        AccentColorHex = "#336699",
        LightPalette = new CustomThemePalette { MainCustomThemeColor = "#D9F2F3F5", ReadingPaneBackgroundColorBrush = "#FFFFFFFF" },
        DarkPalette = new CustomThemePalette { MainCustomThemeColor = "#E61F2430" },
        WallpaperFit = ThemeWallpaperFit.Fill,
        WallpaperAlignment = ThemeWallpaperAlignment.TopLeft
    };

    [Fact]
    public void Root_IsTheCustomThemesFolderUnderApplicationData()
    {
        CreateStore().RootPath.Should().Be(Path.Combine(_dataRoot, "CustomThemes"));
    }

    [Fact]
    public async Task SaveAsync_WritesTheWindowsLayoutAndJsonShape()
    {
        var store = CreateStore();
        var metadata = CreateMetadata();
        byte[] wallpaper = [1, 2, 3];
        byte[] preview = [4, 5];

        await store.SaveAsync(metadata, wallpaper, preview);

        var folder = Path.Combine(_dataRoot, "CustomThemes");
        File.ReadAllBytes(Path.Combine(folder, $"{metadata.Id}.jpg")).Should().Equal(wallpaper);
        File.ReadAllBytes(Path.Combine(folder, $"{metadata.Id}_preview.jpg")).Should().Equal(preview);

        // Byte-for-byte what the Windows NewThemeService serializes for the same metadata.
        var json = File.ReadAllText(Path.Combine(folder, $"{metadata.Id}.json"));
        json.Should().Be(JsonSerializer.Serialize(metadata, DomainModelsJsonContext.Default.CustomThemeMetadata));
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("Name").GetString().Should().Be("Harbour");
        document.RootElement.GetProperty("LightPalette").GetProperty("MainCustomThemeColor").GetString().Should().Be("#D9F2F3F5");

        Directory.GetFiles(folder, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_RoundTripsMetadata()
    {
        var store = CreateStore();
        var metadata = CreateMetadata();
        await store.SaveAsync(metadata, [1], null);

        var loaded = await store.GetAsync(metadata.Id);

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be(metadata.Name);
        loaded.AccentColorHex.Should().Be("#336699");
        loaded.WallpaperAlignment.Should().Be(ThemeWallpaperAlignment.TopLeft);
        loaded.LightPalette!.ReadingPaneBackgroundColorBrush.Should().Be("#FFFFFFFF");
        loaded.DarkPalette!.MainCustomThemeColor.Should().Be("#E61F2430");
    }

    [Fact]
    public async Task SaveAsync_OverwritesWithoutLeavingTemporaryFiles()
    {
        var store = CreateStore();
        var metadata = CreateMetadata();
        await store.SaveAsync(metadata, [1, 1], [2]);

        metadata.Name = "Renamed";
        await store.SaveAsync(metadata, [9], null);

        (await store.GetAsync(metadata.Id))!.Name.Should().Be("Renamed");
        File.ReadAllBytes(store.WallpaperPath(metadata.Id)).Should().Equal(new byte[] { 9 });
        File.ReadAllBytes(store.PreviewPath(metadata.Id)).Should().Equal(new byte[] { 2 });
        Directory.GetFiles(store.RootPath, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_SkipsMalformedMetadataAndTemporaryFiles()
    {
        var store = CreateStore();
        var good = CreateMetadata();
        await store.SaveAsync(good, [1], null);
        File.WriteAllText(Path.Combine(store.RootPath, $"{Guid.NewGuid()}.json"), "{ not json");
        File.WriteAllText(Path.Combine(store.RootPath, $"{Guid.NewGuid()}.json.{Guid.NewGuid():N}.tmp"),
            JsonSerializer.Serialize(CreateMetadata("Leftover"), DomainModelsJsonContext.Default.CustomThemeMetadata));

        var themes = await store.ListAsync();

        themes.Should().ContainSingle().Which.Id.Should().Be(good.Id);
    }

    [Fact]
    public async Task ListAsync_MissingFolder_IsEmpty()
    {
        (await CreateStore().ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_RemovesAllThreeFiles()
    {
        var store = CreateStore();
        var metadata = CreateMetadata();
        await store.SaveAsync(metadata, [1], [2]);

        (await store.DeleteAsync(metadata.Id)).Should().BeTrue();

        File.Exists(store.MetadataPath(metadata.Id)).Should().BeFalse();
        File.Exists(store.WallpaperPath(metadata.Id)).Should().BeFalse();
        File.Exists(store.PreviewPath(metadata.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task MissingTheme_IsNullAndCannotBeDeleted()
    {
        var store = CreateStore();

        (await store.GetAsync(Guid.NewGuid())).Should().BeNull();
        (await store.DeleteAsync(Guid.NewGuid())).Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
            Directory.Delete(_dataRoot, recursive: true);
    }
}
