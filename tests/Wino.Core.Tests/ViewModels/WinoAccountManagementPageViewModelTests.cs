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
            Mock.Of<ISemanticIndexCoordinator>());

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
            ProviderType = MailProviderType.Outlook
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
        var viewModel = new WinoAccountManagementPageViewModel(
            profileService.Object,
            Mock.Of<IWinoAccountDataSyncService>(),
            Mock.Of<IMailDialogService>(),
            billingService.Object,
            apiClient.Object,
            accountService.Object,
            Mock.Of<ISemanticIndexCoordinator>());

        viewModel.OnNavigatedTo(NavigationMode.New, null!);

        await WaitUntilAsync(() => viewModel.IntelligenceMailboxes.Count == 2);

        viewModel.HasIntelligenceAccess.Should().BeTrue();
        viewModel.IntelligenceUsagePercentage.Should().Be(42.5);
        viewModel.IntelligenceMailboxes.Single(x => x.Address == localAccount.Address).CanManage.Should().BeTrue();
        viewModel.IntelligenceMailboxes.Single(x => x.Address == "remote@example.com").CanManage.Should().BeFalse();
        apiClient.Verify(x => x.GetIntelligenceStatusAsync(localMailboxId, It.IsAny<CancellationToken>()), Times.Once);
        apiClient.Verify(x => x.GetIntelligenceStatusAsync(remoteMailboxId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConsentPage_MergesLocalAndServerMailboxes_ByAddressAndProvider()
    {
        var localAccount = new MailAccount
        {
            Id = Guid.NewGuid(),
            Address = "same@example.com",
            Name = "Local",
            ProviderType = MailProviderType.Outlook,
        };
        var serverMailboxId = Guid.NewGuid();
        var apiClient = new Mock<IWinoAccountApiClient>();
        apiClient.Setup(x => x.GetTransportConsentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new TransportConsentDto(ConsentStatuses.NotAccepted, "transport-v1", null, null, null, "https://www.winomail.app/privacy"));
        apiClient.Setup(x => x.GetProcessConsentsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new ProcessConsentListDto("process-v1", "https://www.winomail.app/privacy",
            [
                new MailboxProcessConsentDto(serverMailboxId, "same@example.com", (int)MailProviderType.Outlook,
                    ConsentStatuses.Active, "process-v1", "process-v1", DateTimeOffset.UtcNow, null, null, "https://www.winomail.app/privacy"),
                new MailboxProcessConsentDto(Guid.NewGuid(), "same@example.com", (int)MailProviderType.Gmail,
                    ConsentStatuses.NotAccepted, "process-v1", null, null, null, null, "https://www.winomail.app/privacy"),
            ]));
        var accountService = new Mock<IAccountService>();
        accountService.Setup(x => x.GetAccountsAsync()).ReturnsAsync([localAccount]);
        var viewModel = new WinoAccountConsentPageViewModel(apiClient.Object, accountService.Object, Mock.Of<ISemanticIndexCoordinator>());

        viewModel.OnNavigatedTo(NavigationMode.New, null!);
        await WaitUntilAsync(() => viewModel.Mailboxes.Count == 2);

        var outlook = viewModel.Mailboxes.Single(x => x.ProviderType == MailProviderType.Outlook);
        outlook.LocalAccountId.Should().Be(localAccount.Id);
        outlook.MailboxId.Should().Be(serverMailboxId);
        outlook.IsProcessConsentGranted.Should().BeTrue();
        viewModel.Mailboxes.Single(x => x.ProviderType == MailProviderType.Gmail).LocalAccountId.Should().BeNull();
    }

    [Fact]
    public async Task ConsentPage_TransportConsent_IsAccountWide()
    {
        const string policyVersion = "transport-v1";
        var apiClient = new Mock<IWinoAccountApiClient>();
        apiClient.Setup(x => x.GetTransportConsentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new TransportConsentDto(ConsentStatuses.NotAccepted, policyVersion, null, null, null, "https://www.winomail.app/privacy"));
        apiClient.Setup(x => x.GetProcessConsentsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new ProcessConsentListDto("process-v1", "https://www.winomail.app/privacy", []));
        apiClient.Setup(x => x.AcceptTransportConsentAsync(policyVersion, ConsentActionSources.ConsentPage, It.IsAny<CancellationToken>())).ReturnsAsync(
            new TransportConsentDto(ConsentStatuses.Active, policyVersion, policyVersion, DateTimeOffset.UtcNow, null, "https://www.winomail.app/privacy"));
        var accountService = new Mock<IAccountService>();
        accountService.Setup(x => x.GetAccountsAsync()).ReturnsAsync([]);
        var viewModel = new WinoAccountConsentPageViewModel(apiClient.Object, accountService.Object, Mock.Of<ISemanticIndexCoordinator>());
        await viewModel.LoadAsync();

        (await viewModel.SetTransportConsentAsync(true)).Should().BeTrue();

        viewModel.IsTransportConsentGranted.Should().BeTrue();
        apiClient.Verify(x => x.AcceptTransportConsentAsync(policyVersion, ConsentActionSources.ConsentPage, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConsentPage_ProcessRevocation_DeletesLocalDataAndDisablesIndexing()
    {
        var mailboxId = Guid.NewGuid();
        var localAccount = new MailAccount
        {
            Id = Guid.NewGuid(),
            Address = "mail@example.com",
            Name = "Mail",
            ProviderType = MailProviderType.IMAP4,
            Preferences = new MailAccountPreferences { IsSemanticIndexingEnabled = true },
        };
        var consent = new MailboxProcessConsentDto(mailboxId, localAccount.Address, (int)localAccount.ProviderType,
            ConsentStatuses.Active, "process-v1", "process-v1", DateTimeOffset.UtcNow, null, null, "https://www.winomail.app/privacy");
        var apiClient = new Mock<IWinoAccountApiClient>();
        apiClient.Setup(x => x.GetTransportConsentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new TransportConsentDto(ConsentStatuses.Active, "transport-v1", "transport-v1", DateTimeOffset.UtcNow, null, "https://www.winomail.app/privacy"));
        apiClient.Setup(x => x.GetProcessConsentsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new ProcessConsentListDto("process-v1", "https://www.winomail.app/privacy", [consent]));
        apiClient.Setup(x => x.RevokeProcessConsentAsync(mailboxId, ConsentActionSources.ConsentPage, It.IsAny<CancellationToken>())).ReturnsAsync(
            consent with { Status = ConsentStatuses.Revoked, RevokedAtUtc = DateTimeOffset.UtcNow, DeletionStatus = "completed" });
        var accountService = new Mock<IAccountService>();
        accountService.Setup(x => x.GetAccountsAsync()).ReturnsAsync([localAccount]);
        accountService.Setup(x => x.GetAccountAsync(localAccount.Id)).ReturnsAsync(localAccount);
        var coordinator = new Mock<ISemanticIndexCoordinator>();
        var viewModel = new WinoAccountConsentPageViewModel(apiClient.Object, accountService.Object, coordinator.Object);
        await viewModel.LoadAsync();
        var item = viewModel.Mailboxes.Single();

        (await viewModel.SetProcessConsentAsync(item, false)).Should().BeFalse();

        item.IsProcessConsentGranted.Should().BeFalse();
        localAccount.Preferences.IsSemanticIndexingEnabled.Should().BeFalse();
        coordinator.Verify(x => x.DeleteLocalIndexAsync(localAccount.Id, It.IsAny<CancellationToken>()), Times.Once);
        accountService.Verify(x => x.UpdateAccountAsync(localAccount), Times.Once);
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
            Mock.Of<ISemanticIndexCoordinator>());
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
