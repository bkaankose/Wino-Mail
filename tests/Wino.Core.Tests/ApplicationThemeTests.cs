using System;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Personalization;
using Wino.Core.Domain.Models.Settings;
using Wino.Messaging.Client.Navigation;
using Xunit;

namespace Wino.Core.Tests;

public sealed class ApplicationThemeTests
{
    [Fact]
    public void GalleryFilter_ExcludesCurrentAndAppliesCompatibility()
    {
        var current = Theme(AppThemeType.System, ThemeCompatibility.Both);
        var light = Theme(AppThemeType.PreDefined, ThemeCompatibility.Light);
        var dark = Theme(AppThemeType.PreDefined, ThemeCompatibility.Dark);
        var both = Theme(AppThemeType.System, ThemeCompatibility.Both);
        var custom = Theme(AppThemeType.Custom, ThemeCompatibility.Both);
        var themes = new[] { current, light, dark, both, custom };

        ThemeGalleryFilterPolicy.Apply(themes, current.Id, ThemeGalleryFilter.All)
            .Should().BeEquivalentTo(new[] { light, dark, both, custom });
        ThemeGalleryFilterPolicy.Apply(themes, current.Id, ThemeGalleryFilter.Light)
            .Should().BeEquivalentTo(new[] { light, both });
        ThemeGalleryFilterPolicy.Apply(themes, current.Id, ThemeGalleryFilter.Dark)
            .Should().BeEquivalentTo(new[] { dark, both });
        ThemeGalleryFilterPolicy.Apply(themes, current.Id, ThemeGalleryFilter.Both)
            .Should().ContainSingle().Which.Should().Be(both);
        ThemeGalleryFilterPolicy.Apply(themes, current.Id, ThemeGalleryFilter.Custom)
            .Should().ContainSingle().Which.Should().Be(custom);
    }

    [Fact]
    public void Palette_ResolvesFromBaseAndResetRestoresInheritance()
    {
        var palette = new CustomThemePalette
        {
            MainCustomThemeColor = "#CC112233",
            ReadingPaneBackgroundColorBrush = "#FF445566"
        };

        var resolved = palette.Resolve(isDark: false);
        resolved.WinoContentZoneBackgroud.Should().Be("#CC112233");
        resolved.CalendarDefaultHourBackgroundBrush.Should().Be("#8C112233");
        resolved.ReadingPaneBackgroundColorBrush.Should().Be("#FF445566");

        palette.ResetOverride(CustomThemeColorKey.ReadingPane);
        palette.Resolve(isDark: false).ReadingPaneBackgroundColorBrush.Should().Be("#CC112233");
    }

    [Fact]
    public void LegacyMetadata_UsesPaletteAndWallpaperDefaults()
    {
        const string json = """{"Id":"39de5702-45be-4b99-a4bb-7af2bf35d019","Name":"Legacy","AccentColorHex":"#0078D4"}""";

        var metadata = JsonSerializer.Deserialize(json, DomainModelsJsonContext.Default.CustomThemeMetadata);

        metadata.Should().NotBeNull();
        metadata!.LightPalette.Should().BeNull();
        metadata.DarkPalette.Should().BeNull();
        metadata.WallpaperFit.Should().Be(ThemeWallpaperFit.Fill);
        metadata.WallpaperAlignment.Should().Be(ThemeWallpaperAlignment.Center);
    }

    [Fact]
    public void SaveValidation_RequiresCreateWallpaperButLetsEditKeepAssets()
    {
        var id = Guid.NewGuid();
        var existing = new CustomThemeMetadata { Id = id, Name = "Ocean" };
        var create = Request(null, "New", wallpaper: null);
        var edit = Request(id, " ocean ", wallpaper: null);

        CustomThemeSaveValidator.Validate(create, new[] { existing })
            .Should().Be(CustomThemeValidationError.MissingWallpaper);
        CustomThemeSaveValidator.Validate(edit, new[] { existing })
            .Should().Be(CustomThemeValidationError.None);
        CustomThemeSaveValidator.Validate(Request(null, "OCEAN", new byte[] { 1 }), new[] { existing })
            .Should().Be(CustomThemeValidationError.DuplicateName);
    }

    [Fact]
    public void NestedSettingsResult_PreservesPayloadAndPersonalizationRoot()
    {
        var id = Guid.NewGuid();
        var result = NavigationResult.Saved(id);
        var message = new BackBreadcrumNavigationRequested(Result: result);

        message.Result.Should().BeSameAs(result);
        message.Result!.Payload.Should().Be(id);
        SettingsNavigationInfoProvider.GetRootPage(WinoPage.ApplicationThemeEditorPage)
            .Should().Be(WinoPage.PersonalizationPage);
        SettingsNavigationInfoProvider.GetRootPage(WinoPage.ApplicationThemeGalleryPage)
            .Should().Be(WinoPage.PersonalizationPage);
    }

    private static CustomThemeSaveRequest Request(Guid? id, string name, byte[]? wallpaper)
        => new(id, name, string.Empty, wallpaper, null, null, ThemeWallpaperFit.Fill, ThemeWallpaperAlignment.Center);

    private static TestTheme Theme(AppThemeType type, ThemeCompatibility compatibility)
        => new(type, compatibility);

    private sealed class TestTheme : AppThemeBase
    {
        private readonly AppThemeType _type;

        public TestTheme(AppThemeType type, ThemeCompatibility compatibility) : base(type.ToString(), Guid.NewGuid())
        {
            _type = type;
            Compatibility = compatibility;
        }

        public override AppThemeType AppThemeType => _type;
        public override Task<string> GetThemeResourceDictionaryContentAsync() => Task.FromResult(string.Empty);
        public override string GetBackgroundPreviewImagePath() => string.Empty;
    }
}
