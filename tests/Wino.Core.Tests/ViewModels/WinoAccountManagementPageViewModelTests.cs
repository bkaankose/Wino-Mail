using FluentAssertions;
using Moq;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Mail.Api.Contracts.Billing;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.Contracts.Intelligence;
using Wino.Mail.Contracts.SemanticIndex;
using Xunit;

namespace Wino.Core.Tests.ViewModels;

public sealed class WinoAccountManagementPageViewModelTests
{
    [Fact]
    public async Task CheckoutCompletedNavigation_ForcesOneProfileAndBillingRefresh()
    {
        var account = new WinoAccount
        {
            Id = Guid.NewGuid(),
            Email = "checkout@example.com",
            AccessToken = "access-token",
            AccessTokenExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        var profileService = new Mock<IWinoAccountProfileService>();
        profileService.Setup(x => x.GetActiveAccountAsync()).ReturnsAsync(account);
        profileService
            .Setup(x => x.GetAuthenticatedAccountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);
        profileService
            .Setup(x => x.RefreshProfileAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(WinoAccountOperationResult.Success(account));

        var billingRefreshCompleted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var billingService = new Mock<IWinoBillingService>();
        billingService
            .Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                billingRefreshCompleted.TrySetResult(true);
                return Task.FromResult(ApiEnvelope<BillingStatusResultDto>.Failure("NOT_READY"));
            });
        billingService
            .Setup(x => x.HasUnlimitedAccountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var viewModel = new WinoAccountManagementPageViewModel(
            profileService.Object,
            Mock.Of<IWinoAccountDataSyncService>(),
            Mock.Of<IMailDialogService>(),
            billingService.Object,
            Mock.Of<IWinoAccountApiClient>(),
            Mock.Of<IAccountService>(),
            Mock.Of<ISemanticIndexCoordinator>(),
            Mock.Of<IPreferencesService>(),
            Mock.Of<IAiActionOptionsService>());

        viewModel.OnNavigatedTo(
            NavigationMode.New,
            WinoAccountManagementActivationReason.CheckoutCompleted);

        await billingRefreshCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        profileService.Verify(
            x => x.RefreshProfileAsync(It.IsAny<CancellationToken>()),
            Times.Once);
        billingService.Verify(
            x => x.GetStatusAsync(It.IsAny<CancellationToken>()),
            Times.Once);
        viewModel.IsSignedIn.Should().BeTrue();
    }

    [Fact]
    public async Task ActiveIntelligenceSubscription_LoadsServerUsageAndMailboxData()
    {
        var account = new WinoAccount
        {
            Id = Guid.NewGuid(),
            Email = "intelligence@example.com",
            AccessToken = "access-token",
            AccessTokenExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        var localAccountId = Guid.NewGuid();
        var localAccount = new MailAccount
        {
            Id = localAccountId,
            Address = "local@example.com",
            Name = "Local",
            ProviderType = MailProviderType.Outlook,
            Preferences = new MailAccountPreferences { IsSemanticIndexingEnabled = true }
        };
        var localMailboxId = Guid.NewGuid();
        var remoteMailboxId = Guid.NewGuid();
        var profileService = new Mock<IWinoAccountProfileService>();
        profileService.Setup(x => x.GetActiveAccountAsync()).ReturnsAsync(account);
        profileService.Setup(x => x.GetAuthenticatedAccountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(account);

        var billingService = new Mock<IWinoBillingService>();
        billingService.Setup(x => x.HasUnlimitedAccountsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        billingService.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            ApiEnvelope<BillingStatusResultDto>.Success(new BillingStatusResultDto(
                false,
                new AiPackBillingStatusDto("active", true, null, null, null, false))));

        var apiClient = new Mock<IWinoAccountApiClient>();
        apiClient.Setup(x => x.GetIntelligenceConsentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new IntelligenceConsentDto(ConsentStatuses.Active, "intelligence-v1", "intelligence-v1",
                DateTimeOffset.UtcNow, null, "https://www.winomail.app/privacy", IntelligenceDeletionStatuses.NotRequired));
        apiClient.Setup(x => x.GetAiUsageAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            ApiEnvelope<AiUsageStatusDto>.Success(new AiUsageStatusDto
            {
                EntitlementStatus = "active",
                UsagePercentage = 42.5m,
                RemainingPercentage = 57.5m,
                IsExhausted = false
            }));
        apiClient.Setup(x => x.GetSemanticMailboxesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new SemanticMailboxDto(localMailboxId, localAccount.Address, (int)localAccount.ProviderType, null),
            new SemanticMailboxDto(remoteMailboxId, "remote@example.com", (int)MailProviderType.Gmail, null)
        ]);
        apiClient.Setup(x => x.GetIntelligenceStatusAsync(localMailboxId, It.IsAny<CancellationToken>())).ReturnsAsync(
            CreateIntelligenceStatus(localMailboxId, 1024));
        apiClient.Setup(x => x.GetIntelligenceStatusAsync(remoteMailboxId, It.IsAny<CancellationToken>())).ReturnsAsync(
            CreateIntelligenceStatus(remoteMailboxId, 2048));

        var accountService = new Mock<IAccountService>();
        accountService.Setup(x => x.GetAccountsAsync()).ReturnsAsync([localAccount]);
        accountService.Setup(x => x.GetAccountAsync(localAccountId)).ReturnsAsync(localAccount);
        var coordinator = new Mock<ISemanticIndexCoordinator>();
        var viewModel = new WinoAccountManagementPageViewModel(
            profileService.Object,
            Mock.Of<IWinoAccountDataSyncService>(),
            Mock.Of<IMailDialogService>(),
            billingService.Object,
            apiClient.Object,
            accountService.Object,
            coordinator.Object,
            Mock.Of<IPreferencesService>(),
            Mock.Of<IAiActionOptionsService>());

        viewModel.OnNavigatedTo(NavigationMode.New, null!);

        await WaitUntilAsync(() => viewModel.IntelligenceMailboxes.Count == 2);

        viewModel.HasIntelligenceAccess.Should().BeTrue();
        viewModel.IntelligenceUsagePercentage.Should().Be(42.5);
        viewModel.IntelligenceMailboxes.Single(x => x.Address == localAccount.Address).CanManage.Should().BeTrue();
        viewModel.IntelligenceMailboxes.Single(x => x.Address == "remote@example.com").CanManage.Should().BeFalse();
        apiClient.Verify(x => x.GetIntelligenceStatusAsync(localMailboxId, It.IsAny<CancellationToken>()), Times.Once);
        apiClient.Verify(x => x.GetIntelligenceStatusAsync(remoteMailboxId, It.IsAny<CancellationToken>()), Times.Once);

        var localItem = viewModel.IntelligenceMailboxes.Single(x => x.Address == localAccount.Address);
        await viewModel.ToggleIntelligenceMailboxCommand.ExecuteAsync(localItem);

        coordinator.Verify(x => x.DeleteIndexAsync(localAccountId, It.IsAny<CancellationToken>()), Times.Once);
        coordinator.Verify(x => x.DeleteLocalIndexAsync(localAccountId, It.IsAny<CancellationToken>()), Times.Never);
        localAccount.Preferences.IsSemanticIndexingEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task IntelligencePage_AcceptsOneAccountWideConsent()
    {
        const string policyVersion = "intelligence-v1";
        var apiClient = new Mock<IWinoAccountApiClient>();
        var notAccepted = new IntelligenceConsentDto(ConsentStatuses.NotAccepted, policyVersion, null, null, null,
            "https://www.winomail.app/privacy", IntelligenceDeletionStatuses.NotRequired);
        var accepted = new IntelligenceConsentDto(ConsentStatuses.Active, policyVersion, policyVersion, DateTimeOffset.UtcNow, null,
            "https://www.winomail.app/privacy", IntelligenceDeletionStatuses.NotRequired);
        apiClient.SetupSequence(x => x.GetIntelligenceConsentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(notAccepted)
            .ReturnsAsync(accepted);
        apiClient.Setup(x => x.AcceptIntelligenceConsentAsync(policyVersion, ConsentActionSources.ConsentPage, It.IsAny<CancellationToken>())).ReturnsAsync(
            accepted);
        var viewModel = CreateConsentViewModel(apiClient, Mock.Of<IAccountService>(), Mock.Of<ISemanticIndexCoordinator>());
        viewModel.OnNavigatedTo(NavigationMode.New, null!);
        await WaitUntilAsync(() => viewModel.ConsentPolicyUri != null);

        (await viewModel.SetIntelligenceConsentAsync(true)).Should().BeTrue();

        viewModel.IsConsentGranted.Should().BeTrue();
        apiClient.Verify(x => x.AcceptIntelligenceConsentAsync(
            policyVersion, ConsentActionSources.ConsentPage, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IntelligencePage_RevocationClearsAndDisablesEveryLocalMailbox()
    {
        var accounts = new[]
        {
            new MailAccount { Id = Guid.NewGuid(), Address = "one@example.com", Preferences = new MailAccountPreferences { IsSemanticIndexingEnabled = true } },
            new MailAccount { Id = Guid.NewGuid(), Address = "two@example.com", Preferences = new MailAccountPreferences { IsSemanticIndexingEnabled = true } },
        };
        var active = new IntelligenceConsentDto(ConsentStatuses.Active, "intelligence-v1", "intelligence-v1",
            DateTimeOffset.UtcNow, null, "https://www.winomail.app/privacy", IntelligenceDeletionStatuses.NotRequired);
        var apiClient = new Mock<IWinoAccountApiClient>();
        apiClient.Setup(x => x.GetIntelligenceConsentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(active);
        apiClient.Setup(x => x.RevokeIntelligenceConsentAsync(ConsentActionSources.ConsentPage, It.IsAny<CancellationToken>())).ReturnsAsync(
            active with { Status = ConsentStatuses.Revoked, RevokedAtUtc = DateTimeOffset.UtcNow, DataDeletionStatus = IntelligenceDeletionStatuses.Completed });
        var accountService = new Mock<IAccountService>();
        accountService.Setup(x => x.GetAccountsAsync()).ReturnsAsync(accounts.ToList());
        var coordinator = new Mock<ISemanticIndexCoordinator>();
        var viewModel = CreateConsentViewModel(apiClient, accountService.Object, coordinator.Object);
        viewModel.OnNavigatedTo(NavigationMode.New, null!);
        await WaitUntilAsync(() => viewModel.IsConsentGranted);

        (await viewModel.SetIntelligenceConsentAsync(false)).Should().BeFalse();

        accounts.Should().OnlyContain(x => !x.Preferences.IsSemanticIndexingEnabled);
        foreach (var account in accounts)
            coordinator.Verify(x => x.DeleteLocalIndexAsync(account.Id, It.IsAny<CancellationToken>()), Times.Once);
        accountService.Verify(x => x.UpdateAccountAsync(It.IsAny<MailAccount>()), Times.Exactly(2));
    }

    [Fact]
    public async Task AccountUsage_ReportsRemainingHeadroom_WhenUnlimitedAccountsIsNotOwned()
    {
        var viewModel = CreateViewModelWithAccounts(
            hasUnlimitedAccounts: false,
            mailAccountCount: 2,
            out _);

        viewModel.OnNavigatedTo(NavigationMode.New, null!);

        await WaitUntilAsync(() => viewModel.AccountUsagePercentage > 0);

        viewModel.AccountUsageText.Should().Be(
            string.Format(Translator.WinoAccount_Management_AccountUsage, 2, Constants.FreeAccountLimit));
        viewModel.AccountUsagePercentage.Should().BeApproximately(200d / 3d, 0.001);
    }

    [Fact]
    public async Task AccountUsage_DropsTheMeter_WhenUnlimitedAccountsIsOwned()
    {
        var viewModel = CreateViewModelWithAccounts(
            hasUnlimitedAccounts: true,
            mailAccountCount: 2,
            out _);

        viewModel.OnNavigatedTo(NavigationMode.New, null!);

        await WaitUntilAsync(() => viewModel.UnlimitedAccountsAddOn.IsPurchased);

        viewModel.AccountUsageText.Should().Be(
            string.Format(Translator.WinoAccount_Management_AccountUsageUnlimited, 2));

        // The limit no longer applies, so a filled meter would be actively misleading.
        viewModel.AccountUsagePercentage.Should().Be(0);
    }

    [Fact]
    public async Task AiPackFooter_AnnouncesCancellation_WhenSubscriptionLapsesAtPeriodEnd()
    {
        var periodStart = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var periodEnd = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var viewModel = CreateViewModelWithAccounts(
            hasUnlimitedAccounts: false,
            mailAccountCount: 0,
            out _,
            aiPack: new AiPackBillingStatusDto("active", true, periodStart, periodEnd, periodEnd, true));

        viewModel.OnNavigatedTo(NavigationMode.New, null!);

        await WaitUntilAsync(() => viewModel.AiPackAddOn.IsPurchased);

        viewModel.AiPackBillingPeriodText.Should().Be(string.Format(
            Translator.WinoAccount_Management_AiPackBillingPeriodValue,
            periodStart.LocalDateTime,
            periodEnd.LocalDateTime));

        viewModel.AiPackRenewalOrCancellationText.Should().Be(string.Format(
            Translator.WinoAccount_Management_AiPackCancels,
            periodEnd.LocalDateTime));
    }

    private static WinoAccountManagementPageViewModel CreateViewModelWithAccounts(
        bool hasUnlimitedAccounts,
        int mailAccountCount,
        out Mock<IWinoAccountApiClient> apiClient,
        AiPackBillingStatusDto? aiPack = null)
    {
        var account = new WinoAccount
        {
            Id = Guid.NewGuid(),
            Email = "usage@example.com",
            AccessToken = "access-token",
            AccessTokenExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        var profileService = new Mock<IWinoAccountProfileService>();
        profileService.Setup(x => x.GetActiveAccountAsync()).ReturnsAsync(account);
        profileService.Setup(x => x.GetAuthenticatedAccountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(account);

        var billingService = new Mock<IWinoBillingService>();
        billingService.Setup(x => x.HasUnlimitedAccountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(hasUnlimitedAccounts);
        billingService.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            ApiEnvelope<BillingStatusResultDto>.Success(new BillingStatusResultDto(
                hasUnlimitedAccounts,
                aiPack ?? new AiPackBillingStatusDto("inactive", false, null, null, null, false))));

        apiClient = new Mock<IWinoAccountApiClient>();
        apiClient.Setup(x => x.GetSemanticMailboxesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var accounts = Enumerable.Range(0, mailAccountCount)
            .Select(index => new MailAccount
            {
                Id = Guid.NewGuid(),
                Address = $"mailbox{index}@example.com",
                Name = $"Mailbox {index}",
                ProviderType = MailProviderType.Outlook
            })
            .ToList();
        var accountService = new Mock<IAccountService>();
        accountService.Setup(x => x.GetAccountsAsync()).ReturnsAsync(accounts);

        return new WinoAccountManagementPageViewModel(
            profileService.Object,
            Mock.Of<IWinoAccountDataSyncService>(),
            Mock.Of<IMailDialogService>(),
            billingService.Object,
            apiClient.Object,
            accountService.Object,
            Mock.Of<ISemanticIndexCoordinator>(),
            Mock.Of<IPreferencesService>(),
            Mock.Of<IAiActionOptionsService>());
    }

    private static WinoAccountManagementPageViewModel CreateConsentViewModel(
        Mock<IWinoAccountApiClient> apiClient,
        IAccountService accountService,
        ISemanticIndexCoordinator coordinator)
    {
        var account = new WinoAccount
        {
            Id = Guid.NewGuid(),
            Email = "consent@example.com",
            AccessToken = "access-token",
            AccessTokenExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        var profileService = new Mock<IWinoAccountProfileService>();
        profileService.Setup(x => x.GetActiveAccountAsync()).ReturnsAsync(account);
        profileService.Setup(x => x.GetAuthenticatedAccountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(account);
        var billingService = new Mock<IWinoBillingService>();
        billingService.Setup(x => x.HasUnlimitedAccountsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        billingService.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            ApiEnvelope<BillingStatusResultDto>.Success(new BillingStatusResultDto(
                false,
                new AiPackBillingStatusDto("active", true, null, null, null, false))));
        apiClient.Setup(x => x.GetSemanticMailboxesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        return new WinoAccountManagementPageViewModel(
            profileService.Object,
            Mock.Of<IWinoAccountDataSyncService>(),
            Mock.Of<IMailDialogService>(),
            billingService.Object,
            apiClient.Object,
            accountService,
            coordinator,
            Mock.Of<IPreferencesService>(),
            Mock.Of<IAiActionOptionsService>());
    }

    private static IntelligenceMailboxStatusDto CreateIntelligenceStatus(Guid mailboxId, long size)
        => new(mailboxId, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, size,
            "openai-text-embedding-3-small-768-v2", EmbeddingModelStatuses.Current, "en-US");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < timeout)
        {
            await Task.Delay(25);
        }

        condition().Should().BeTrue();
    }
}
