using FluentAssertions;
using Moq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class CompanionSettingsPageViewModelTests
{
    [Fact]
    public void DisablingTheCompanion_WritesThePreferenceAndDisablesDependentSettings()
    {
        var preferences = new Mock<IPreferencesService>();
        preferences.SetupProperty(service => service.IsCompanionEnabled, true);
        preferences.SetupProperty(service => service.ShowUnreadMailInCompanion, true);
        var viewModel = new CompanionSettingsPageViewModel(preferences.Object);
        var raised = new List<string>();
        viewModel.PropertyChanged += (_, args) => raised.Add(args.PropertyName!);

        viewModel.IsUnreadBehaviorEnabled.Should().BeTrue();
        viewModel.IsCompanionEnabled = false;

        preferences.Object.IsCompanionEnabled.Should().BeFalse();
        viewModel.IsContentEnabled.Should().BeFalse();
        viewModel.IsUnreadBehaviorEnabled.Should().BeFalse();
        raised.Should().Contain([nameof(viewModel.IsCompanionEnabled), nameof(viewModel.IsContentEnabled), nameof(viewModel.IsUnreadBehaviorEnabled)]);
    }

    [Fact]
    public void SelectedUnreadBehavior_ReadsAndWritesThePreference()
    {
        var preferences = new Mock<IPreferencesService>();
        preferences.SetupProperty(service => service.CompanionUnreadMessageBehavior, CompanionUnreadMessageBehavior.AfterAppSession);
        var viewModel = new CompanionSettingsPageViewModel(preferences.Object);

        viewModel.SelectedUnreadBehavior.Behavior.Should().Be(CompanionUnreadMessageBehavior.AfterAppSession);
        viewModel.SelectedUnreadBehavior = viewModel.UnreadBehaviorOptions.Single(option => option.Behavior == CompanionUnreadMessageBehavior.Everything);

        preferences.Object.CompanionUnreadMessageBehavior.Should().Be(CompanionUnreadMessageBehavior.Everything);
    }

    [Fact]
    public void PreferenceChangedElsewhere_WhileShown_RaisesTheToggle()
    {
        var preferences = new Mock<IPreferencesService>();
        var viewModel = new CompanionSettingsPageViewModel(preferences.Object);
        viewModel.OnNavigatedTo(NavigationMode.New, null!);
        var raised = new List<string>();
        viewModel.PropertyChanged += (_, args) => raised.Add(args.PropertyName!);

        preferences.Raise(service => service.PreferenceChanged += null, preferences.Object, nameof(IPreferencesService.ShowTasksInCompanion));
        viewModel.OnNavigatedFrom(NavigationMode.New, null!);
        preferences.Raise(service => service.PreferenceChanged += null, preferences.Object, nameof(IPreferencesService.ShowCalendarInCompanion));

        raised.Should().Equal(nameof(viewModel.ShowTasks));
    }
}
