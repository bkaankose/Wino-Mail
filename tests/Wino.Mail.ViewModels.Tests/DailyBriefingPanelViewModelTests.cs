using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.ViewModels;
using Xunit;
using Wino.Core.Domain;

namespace Wino.Mail.ViewModels.Tests;

public sealed class DailyBriefingPanelViewModelTests
{
    [Fact]
    public async Task InitializeAsync_CachesSevenDatesAndCallsLocalServiceOncePerDate()
    {
        var localService = new Mock<ILocalIntelligenceService>();
        var account = CreateAccount("Work");
        SetupBriefing(localService, [account], [CreateFact(account, "Pay invoice", MailPriority.High)]);
        var viewModel = CreateViewModel(localService);

        await viewModel.InitializeAsync();

        viewModel.Dates.Should().HaveCount(7);
        viewModel.SelectedDate.Should().BeSameAs(viewModel.Dates[0]);
        localService.Verify(service => service.GetBriefingFactsAsync(
            It.IsAny<DateOnly>(), It.IsAny<TimeZoneInfo>(), true, It.IsAny<CancellationToken>()), Times.Exactly(7));
    }

    [Fact]
    public async Task InitializeAsync_GroupsOnlyVisibleAccountsInStableOrder()
    {
        var localService = new Mock<ILocalIntelligenceService>();
        var first = CreateAccount("First");
        var second = CreateAccount("Second");
        SetupBriefing(localService, [first, second],
        [
            CreateFact(second, "Normal", MailPriority.Normal),
            CreateFact(first, "Urgent", MailPriority.Urgent),
        ]);
        var viewModel = CreateViewModel(localService);

        await viewModel.InitializeAsync();

        viewModel.SelectedDateGroups.Should().HaveCount(2);
        viewModel.SelectedDateGroups![0].Account.Account.Should().BeSameAs(first);
        viewModel.SelectedDateGroups[1].Account.Account.Should().BeSameAs(second);
        viewModel.SelectedDateGroups[0].Should().ContainSingle();
        viewModel.SelectedDateGroups[1].Should().ContainSingle();
    }

    [Fact]
    public async Task ChangingDate_UsesCachedGroupsWithoutCallingService()
    {
        var localService = new Mock<ILocalIntelligenceService>();
        var account = CreateAccount("Work");
        SetupBriefing(localService, [account], [CreateFact(account, "Pay invoice", MailPriority.Normal)]);
        var viewModel = CreateViewModel(localService);
        await viewModel.InitializeAsync();
        var calls = 7;

        viewModel.SelectedDateIndex = 3;
        await Task.Yield();

        viewModel.SelectedDate.Should().BeSameAs(viewModel.Dates[3]);
        localService.Verify(service => service.GetBriefingFactsAsync(
            It.IsAny<DateOnly>(), It.IsAny<TimeZoneInfo>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(calls));
    }

    [Fact]
    public async Task IgnoreCommand_RemovesMatchingFactFromEveryCachedDateWithoutReset()
    {
        var localService = new Mock<ILocalIntelligenceService>();
        localService.Setup(service => service.IgnoreBriefingItemAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var account = CreateAccount("Work");
        var target = CreateFact(account, "Pay invoice", MailPriority.High);
        SetupBriefing(localService, [account], [target]);
        var viewModel = CreateViewModel(localService);
        await viewModel.InitializeAsync();
        var item = viewModel.Dates[0].Groups.Single().Single();
        var actions = new List<NotifyCollectionChangedAction>();
        viewModel.Dates[0].Groups.Single().CollectionChanged += (_, args) => actions.Add(args.Action);

        await viewModel.IgnoreCommand.ExecuteAsync(item);

        viewModel.Dates.Should().OnlyContain(date => date.Groups.Count == 0);
        actions.Should().Contain(NotifyCollectionChangedAction.Remove);
        actions.Should().NotContain(NotifyCollectionChangedAction.Reset);
    }

    [Fact]
    public async Task DeleteCommand_RemovesMatchingFactFromEveryCachedDate()
    {
        var localService = new Mock<ILocalIntelligenceService>();
        localService.Setup(service => service.DeleteBriefingItemAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var account = CreateAccount("Work");
        SetupBriefing(localService, [account], [CreateFact(account, "Remove this", MailPriority.Normal)]);
        var viewModel = CreateViewModel(localService);
        await viewModel.InitializeAsync();

        var item = viewModel.Dates[0].Groups.Single().Single();
        await viewModel.DeleteCommand.ExecuteAsync(item);

        viewModel.Dates.Should().OnlyContain(date => date.Groups.Count == 0);
        localService.Verify(service => service.DeleteBriefingItemAsync(
            item.LocalAccountId, item.Fact.RemoteMessageId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ShowIgnored_ChangesAllSevenProjectionsLocallyAndPreservesVisibleItem()
    {
        var localService = new Mock<ILocalIntelligenceService>();
        var account = CreateAccount("Work");
        var visible = CreateFact(account, "Read update", MailPriority.Normal);
        var ignored = CreateFact(account, "Pay invoice", MailPriority.High, isIgnored: true);
        SetupBriefing(localService, [account], [visible, ignored], hasIgnoredFacts: true);
        var viewModel = CreateViewModel(localService);
        await viewModel.InitializeAsync();
        var existing = viewModel.SelectedDateGroups!.Single().Single();

        viewModel.IsShowingIgnored = true;

        viewModel.Dates.Should().OnlyContain(date => date.Groups.Single().Count == 2);
        viewModel.SelectedDateGroups!.Single()[1].Should().BeSameAs(existing);
        localService.Verify(service => service.GetBriefingFactsAsync(
            It.IsAny<DateOnly>(), It.IsAny<TimeZoneInfo>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(7));
    }

    [Fact]
    public async Task UnignoreCommand_ReinsertsHiddenItemAtPriorityIndex()
    {
        var localService = new Mock<ILocalIntelligenceService>();
        localService.Setup(service => service.UnignoreBriefingItemAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var account = CreateAccount("Work");
        var ignored = CreateFact(account, "Pay invoice", MailPriority.High, isIgnored: true);
        var visible = CreateFact(account, "Read update", MailPriority.Normal);
        SetupBriefing(localService, [account], [visible, ignored], hasIgnoredFacts: true);
        var viewModel = CreateViewModel(localService);
        await viewModel.InitializeAsync();
        viewModel.IsShowingIgnored = true;
        var ignoredItem = viewModel.SelectedDateGroups!.Single()[0];
        viewModel.IsShowingIgnored = false;

        await viewModel.IgnoreCommand.ExecuteAsync(ignoredItem);

        viewModel.SelectedDateGroups!.Single().Should().HaveCount(2);
        viewModel.SelectedDateGroups.Single()[0].BriefingId.Should().Be(ignoredItem.BriefingId);
        viewModel.SelectedDateGroups.Single()[0].IsIgnored.Should().BeFalse();
        viewModel.IsFilteredEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task EmptyBriefingId_DoesNotExposeIgnoreCommand()
    {
        var localService = new Mock<ILocalIntelligenceService>();
        var account = CreateAccount("Work");
        SetupBriefing(localService, [account], [CreateFact(account, "Informational", MailPriority.Normal, briefingId: Guid.Empty)]);
        var viewModel = CreateViewModel(localService);
        await viewModel.InitializeAsync();

        var item = viewModel.SelectedDateGroups!.Single().Single();
        item.HasIgnoreAction.Should().BeFalse();
        item.CanToggleIgnore.Should().BeFalse();
    }

    [Fact]
    public void ShowIgnoredPreference_IsLoadedAndSaved()
    {
        var preferences = new Mock<IPreferencesService>();
        preferences.SetupProperty(service => service.IsDailyBriefingShowingIgnored, true);

        var viewModel = CreateViewModel(new Mock<ILocalIntelligenceService>(), preferences);

        viewModel.IsShowingIgnored.Should().BeTrue();
        viewModel.IsShowingIgnored = false;
        preferences.Object.IsDailyBriefingShowingIgnored.Should().BeFalse();
    }

    [Fact]
    public async Task IgnoreFailure_LeavesEveryProjectionUnchanged()
    {
        var localService = new Mock<ILocalIntelligenceService>();
        localService.Setup(service => service.IgnoreBriefingItemAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("disk full"));
        var account = CreateAccount("Work");
        SetupBriefing(localService, [account], [CreateFact(account, "Pay invoice", MailPriority.Normal)]);
        var dialog = new Mock<IMailDialogService>();
        var viewModel = CreateViewModel(localService, dialogOverride: dialog);
        await viewModel.InitializeAsync();
        var item = viewModel.SelectedDateGroups!.Single().Single();
        var group = viewModel.SelectedDateGroups.Single();

        await viewModel.IgnoreCommand.ExecuteAsync(item);

        viewModel.Dates.Should().OnlyContain(date => date.Groups.Single().Single().BriefingId == item.BriefingId);
        item.IsIgnored.Should().BeFalse();
        item.IsIgnorePending.Should().BeFalse();
        dialog.Verify(service => service.InfoBarMessage(
            It.IsAny<string>(), It.Is<string>(message => message.Contains("disk full")), InfoBarMessageType.Error), Times.Once);
    }

    private static DailyBriefingPanelViewModel CreateViewModel(Mock<ILocalIntelligenceService> localService,
        Mock<IPreferencesService>? preferencesOverride = null, Mock<IMailDialogService>? dialogOverride = null)
    {
        var dateContext = new Mock<IDateContextProvider>();
        dateContext.SetupGet(provider => provider.Culture).Returns(CultureInfo.GetCultureInfo("en-US"));
        dateContext.SetupGet(provider => provider.TimeZone).Returns(TimeZoneInfo.Utc);
        dateContext.Setup(provider => provider.GetToday()).Returns(new DateOnly(2026, 8, 14));

        var preferences = preferencesOverride ?? new Mock<IPreferencesService>();
        if (preferencesOverride is null)
            preferences.SetupProperty(service => service.IsDailyBriefingShowingIgnored, false);

        return new(localService.Object, Mock.Of<IClipboardService>(), dateContext.Object, new ImmediateDispatcher(),
            preferences.Object, Mock.Of<INavigationService>(), Mock.Of<IMailService>(), Mock.Of<IMimeFileService>(),
            Mock.Of<IWinoRequestDelegator>(), dialogOverride?.Object ?? Mock.Of<IMailDialogService>());
    }

    private static MailAccount CreateAccount(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        IsMailAccessGranted = true,
    };

    private static DailyBriefingFact CreateFact(MailAccount account, string headline, MailPriority priority,
        Guid? briefingId = null, bool isIgnored = false)
    {
        var occurredAt = new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);
        return new(account.Id, Guid.NewGuid(), $"remote-{Guid.NewGuid():N}", headline, "Sender", occurredAt,
            headline, 1, new ConversationFactPayload
            {
                BriefingId = briefingId ?? Guid.NewGuid(),
                OccurredAtUtc = occurredAt,
                Kind = MessageKind.Conversation,
                Status = BriefingStatus.Informational,
                Urgency = priority,
                PrimaryAction = new NoActionPayload { Confidence = 1 },
                TemporalReferences = [],
                Confidence = 1,
            }, IsIgnored: isIgnored);
    }

    private static void SetupBriefing(Mock<ILocalIntelligenceService> localService, MailAccount[] accounts,
        DailyBriefingFact[] facts, bool hasIgnoredFacts = false)
    {
        localService.Setup(service => service.GetEligibleAccountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(accounts.Select(static account => new DailyBriefingAccount(account)).ToArray());
        localService.Setup(service => service.MarkOpenedAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        localService.Setup(service => service.GetBriefingFactsAsync(
            It.IsAny<DateOnly>(), It.IsAny<TimeZoneInfo>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DailyBriefingFactsResult(facts, hasIgnoredFacts));
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
