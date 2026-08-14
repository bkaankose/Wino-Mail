using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.ViewModels;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class DailyBriefingPanelViewModelTests
{
    [Fact]
    public async Task InitializeAsync_WithNoEligibleLocalAccounts_ShowsUnavailableState()
    {
        var localService = new Mock<ILocalIntelligenceService>();
        localService.Setup(service => service.GetEligibleAccountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        localService.Setup(service => service.MarkOpenedAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var viewModel = CreateViewModel(localService);

        await viewModel.InitializeAsync();

        viewModel.Dates.Should().HaveCount(7);
        viewModel.SelectedDateIndex.Should().Be(0);
        viewModel.IsUnavailable.Should().BeTrue();
        viewModel.IsLoading.Should().BeFalse();
        viewModel.ShowContent.Should().BeFalse();
        localService.Verify(service => service.GetBriefingFactsAsync(
            It.IsAny<DateOnly>(), It.IsAny<TimeZoneInfo>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InitializeAsync_WhenLocalStoreFails_ShowsRetryableError()
    {
        var localService = new Mock<ILocalIntelligenceService>();
        localService.Setup(service => service.GetEligibleAccountsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("local store unavailable"));
        var viewModel = CreateViewModel(localService);

        await viewModel.InitializeAsync();

        viewModel.HasLoadError.Should().BeTrue();
        viewModel.LoadError.Should().NotBeNullOrWhiteSpace();
        viewModel.IsLoading.Should().BeFalse();
        viewModel.ShowContent.Should().BeFalse();
    }

    private static DailyBriefingPanelViewModel CreateViewModel(Mock<ILocalIntelligenceService> localService)
    {
        var dateContext = new Mock<IDateContextProvider>();
        dateContext.SetupGet(provider => provider.Culture).Returns(CultureInfo.GetCultureInfo("en-US"));
        dateContext.SetupGet(provider => provider.TimeZone).Returns(TimeZoneInfo.Utc);
        dateContext.Setup(provider => provider.GetToday()).Returns(new DateOnly(2026, 8, 14));

        var preferences = new Mock<IPreferencesService>();
        preferences.SetupProperty(service => service.IsDailyBriefingGroupedByAccount, true);

        return new(
            localService.Object,
            Mock.Of<IClipboardService>(),
            dateContext.Object,
            new ImmediateDispatcher(),
            preferences.Object,
            Mock.Of<INavigationService>(),
            Mock.Of<IMailService>(),
            Mock.Of<IMimeFileService>(),
            Mock.Of<IWinoRequestDelegator>(),
            Mock.Of<IMailDialogService>());
    }

    private sealed class ImmediateDispatcher : IDispatcher
    {
        public Task ExecuteOnUIThread(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }
}
