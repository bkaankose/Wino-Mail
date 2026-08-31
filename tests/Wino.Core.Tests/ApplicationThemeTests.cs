using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Personalization;
using Wino.Core.Domain.Models.Settings;
using Wino.Core.Domain.Translations;
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
        ThemeGalleryFilterPolicy.Apply(themes, current.Id, ThemeGalleryFilter.Online)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(AppThemeType.System, false)]
    [InlineData(AppThemeType.PreDefined, false)]
    [InlineData(AppThemeType.Custom, true)]
    public void ThemeActions_AreAvailableOnlyForCustomThemes(AppThemeType type, bool expected)
        => Theme(type, ThemeCompatibility.Both).IsCustomTheme.Should().Be(expected);

    [Fact]
    public void GalleryLabels_UseConciseCompatibilityAndCustomCreationWording()
    {
        using var stream = WinoTranslationDictionary.GetLanguageStream(AppLanguage.English);
        var resources = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);

        resources.Should().NotBeNull();
        resources!["ApplicationThemeGallery_Both"].Should().Be("Both");
        resources["ApplicationThemeGallery_Adaptive"].Should().Be("Both");
        resources["ApplicationThemeGallery_Create"].Should().Be("Create custom theme");
        resources["ApplicationThemeGallery_CreateTheme"].Should().Be("Create custom theme");
    }

    [Fact]
    public void OnlineFilter_ShowsOnlineStateWithoutLocalEmptyState()
    {
        var viewModel = new Wino.Core.ViewModels.ApplicationThemeGalleryPageViewModel(
            Mock.Of<INewThemeService>(),
            Mock.Of<IDialogServiceBase>());

        viewModel.SelectedFilter = ThemeGalleryFilter.Online;

        viewModel.IsOnline.Should().BeTrue();
        viewModel.IsLocalGalleryVisible.Should().BeFalse();
        viewModel.IsEmpty.Should().BeFalse();
        viewModel.FilteredThemes.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a uri")]
    public void WallpaperPreview_InvalidOrEmptyPathIsSafe(string? path)
        => ThemeWallpaperPreviewPath.GetAbsoluteUri(path).Should().BeNull();

    [Theory]
    [InlineData("ms-appdata:///local/CustomThemes/example.jpg")]
    [InlineData("file:///C:/Pictures/example.jpg")]
    public void WallpaperPreview_AbsolutePathProducesUri(string path)
        => ThemeWallpaperPreviewPath.GetAbsoluteUri(path).Should().NotBeNull();

    [Fact]
    public async Task ApplyTheme_UpdatesCurrentThemeOnlyAfterSuccessfulApply()
    {
        var current = Theme(AppThemeType.System, ThemeCompatibility.Both);
        var selected = Theme(AppThemeType.PreDefined, ThemeCompatibility.Dark);
        var service = new Mock<INewThemeService>();
        service.SetupGet(candidate => candidate.CurrentApplicationThemeId).Returns(current.Id);
        service.Setup(candidate => candidate.GetAvailableThemesAsync()).ReturnsAsync([current, selected]);
        var viewModel = new Wino.Core.ViewModels.ApplicationThemeGalleryPageViewModel(
            service.Object,
            Mock.Of<IDialogServiceBase>());

        await viewModel.LoadThemesCommand.ExecuteAsync(null);
        await viewModel.ApplyThemeCommand.ExecuteAsync(selected);

        service.Verify(candidate => candidate.SelectThemeAsync(selected.Id, false), Times.Once);
        viewModel.CurrentTheme.Should().BeSameAs(selected);
        viewModel.FilteredThemes.Should().NotContain(selected);
    }

    [Fact]
    public async Task ApplyTheme_FailurePreservesCurrentThemeAndSurfacesError()
    {
        var current = Theme(AppThemeType.System, ThemeCompatibility.Both);
        var selected = Theme(AppThemeType.PreDefined, ThemeCompatibility.Dark);
        var service = new Mock<INewThemeService>();
        service.SetupGet(candidate => candidate.CurrentApplicationThemeId).Returns(current.Id);
        service.Setup(candidate => candidate.GetAvailableThemesAsync()).ReturnsAsync([current, selected]);
        service.Setup(candidate => candidate.SelectThemeAsync(selected.Id, false)).ThrowsAsync(new InvalidOperationException("Apply failed"));
        var viewModel = new Wino.Core.ViewModels.ApplicationThemeGalleryPageViewModel(
            service.Object,
            Mock.Of<IDialogServiceBase>());

        await viewModel.LoadThemesCommand.ExecuteAsync(null);
        await viewModel.ApplyThemeCommand.ExecuteAsync(selected);

        viewModel.CurrentTheme.Should().BeSameAs(current);
        viewModel.FilteredThemes.Should().Contain(selected);
        viewModel.IsApplyError.Should().BeTrue();
        viewModel.ErrorMessage.Should().Be("Apply failed");
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
