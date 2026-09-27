using CommunityToolkit.Mvvm.Messaging;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Authentication;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Services;
using Wino.Messaging.Server;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class AccountReauthenticationServiceTests
{
    [Fact]
    public async Task ReauthenticateAsync_ClearsAttentionStoresSignInAndKeepsActiveFeatureScopes()
    {
        var account = CreateRestoredAccount(MailProviderType.Outlook);
        IReadOnlyCollection<ProviderFeature>? requestedFeatures = null;
        var accountService = CreateAccountService(account);
        var featureService = new Mock<IAccountProviderFeatureService>();
        featureService
            .Setup(service => service.GetFeaturesAsync(account.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AccountProviderFeature>
            {
                new() { MailAccountId = account.Id, Feature = ProviderFeature.MailFilters, AuthorizationState = ProviderFeatureAuthorizationState.Active }
            });
        var synchronizationManager = new Mock<ISynchronizationManager>();
        synchronizationManager
            .Setup(manager => manager.HandleAuthorizationAsync(
                MailProviderType.Outlook,
                account,
                false,
                true,
                It.IsAny<IReadOnlyCollection<ProviderFeature>>()))
            .Callback<MailProviderType, MailAccount, bool, bool, IReadOnlyCollection<ProviderFeature>>(
                (_, _, _, _, features) => requestedFeatures = features)
            .ReturnsAsync(new TokenInformationEx("token", account.Address, "login@example.com"));
        var service = CreateService(accountService, featureService, synchronizationManager, new WeakReferenceMessenger());

        var result = await service.ReauthenticateAsync(account.Id);

        Assert.Same(account, result);
        Assert.Equal(AccountAttentionReason.None, account.AttentionReason);
        Assert.Equal("login@example.com", account.AuthenticationAddress);
        Assert.NotNull(requestedFeatures);
        Assert.Contains(ProviderFeature.MailFilters, requestedFeatures!);
        accountService.Verify(service => service.UpdateAccountAsync(account), Times.Once);
        accountService.Verify(service => service.CreateAccountAsync(It.IsAny<MailAccount>(), It.IsAny<CustomServerInformation?>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
        synchronizationManager.Verify(manager => manager.DestroySynchronizerAsync(account.Id), Times.Once);
    }

    [Fact]
    public async Task ReauthenticateAsync_RequestsEnabledModesEvenWhenNeverGranted()
    {
        var account = CreateRestoredAccount(MailProviderType.Gmail);
        account.IsCalendarAccessEnabled = true;
        account.CalendarIntegrationSource = AccountIntegrationSource.Provider;
        account.IsContactAccessEnabled = true;
        account.IsContactAccessGranted = false;
        account.IsContactReauthorizationRequired = false;
        account.ContactIntegrationSource = AccountIntegrationSource.Provider;
        account.IsTaskAccessEnabled = true;
        account.IsTaskAccessGranted = false;
        account.IsTaskReauthorizationRequired = true;
        account.TaskIntegrationSource = AccountIntegrationSource.Provider;
        var accountService = CreateAccountService(account);
        var synchronizationManager = new Mock<ISynchronizationManager>();
        synchronizationManager
            .Setup(manager => manager.HandleAuthorizationAsync(
                MailProviderType.Gmail,
                account,
                true,
                true,
                It.IsAny<IReadOnlyCollection<ProviderFeature>>()))
            .Callback<MailProviderType, MailAccount, bool, bool, IReadOnlyCollection<ProviderFeature>>(
                (_, requestedAccount, _, _, _) =>
                {
                    // Scopes follow the Granted flags, so they must match Enabled before the sign-in.
                    var request = ProviderAuthorizationRequest.ForAccount(requestedAccount);
                    Assert.True(request.IncludeCalendar);
                    Assert.True(request.IncludeContacts);
                    Assert.True(request.IncludeTasks);
                })
            .ReturnsAsync(new TokenInformationEx("token", account.Address));
        var service = CreateService(accountService, CreateFeatureService(account.Id), synchronizationManager, new WeakReferenceMessenger());

        await service.ReauthenticateAsync(account.Id);

        Assert.True(account.IsCalendarAccessGranted);
        Assert.True(account.IsContactAccessGranted);
        Assert.False(account.IsContactReauthorizationRequired);
        Assert.True(account.IsTaskAccessGranted);
        Assert.False(account.IsTaskReauthorizationRequired);
    }

    [Fact]
    public async Task ReauthenticateAsync_NeverRequestsDisabledOrLocalModes()
    {
        var account = CreateRestoredAccount(MailProviderType.Outlook);
        account.IsCalendarAccessEnabled = false;
        account.IsCalendarAccessGranted = true;
        account.CalendarIntegrationSource = AccountIntegrationSource.Provider;
        account.IsContactAccessEnabled = false;
        account.IsContactAccessGranted = true;
        account.ContactIntegrationSource = AccountIntegrationSource.Provider;
        account.IsTaskAccessEnabled = true;
        account.IsTaskAccessGranted = true;
        account.TaskIntegrationSource = AccountIntegrationSource.Local;
        var accountService = CreateAccountService(account);
        var synchronizationManager = new Mock<ISynchronizationManager>();
        synchronizationManager
            .Setup(manager => manager.HandleAuthorizationAsync(
                MailProviderType.Outlook,
                account,
                false,
                true,
                It.IsAny<IReadOnlyCollection<ProviderFeature>>()))
            .Callback<MailProviderType, MailAccount, bool, bool, IReadOnlyCollection<ProviderFeature>>(
                (_, requestedAccount, _, _, _) =>
                {
                    var request = ProviderAuthorizationRequest.ForAccount(requestedAccount);
                    Assert.True(request.IncludeMail);
                    Assert.False(request.IncludeCalendar);
                    Assert.False(request.IncludeContacts);
                    Assert.False(request.IncludeTasks);
                })
            .ReturnsAsync(new TokenInformationEx("token", account.Address));
        var service = CreateService(accountService, CreateFeatureService(account.Id), synchronizationManager, new WeakReferenceMessenger());

        await service.ReauthenticateAsync(account.Id);

        Assert.False(account.IsCalendarAccessGranted);
        Assert.False(account.IsContactAccessGranted);
        Assert.False(account.IsTaskAccessGranted);
        Assert.True(account.IsTaskAccessEnabled);
    }

    [Fact]
    public async Task ReauthenticateAsync_ReconsentOnlyAccount_GrantsTheWaitingModeInTheSameSignIn()
    {
        var account = CreateRestoredAccount(MailProviderType.Outlook);
        account.AttentionReason = AccountAttentionReason.None;
        account.IsContactAccessEnabled = true;
        account.IsContactAccessGranted = true;
        account.IsContactReauthorizationRequired = true;
        account.ContactIntegrationSource = AccountIntegrationSource.Provider;
        var accountService = CreateAccountService(account);
        var synchronizationManager = new Mock<ISynchronizationManager>();
        synchronizationManager
            .Setup(manager => manager.HandleAuthorizationAsync(
                It.IsAny<MailProviderType>(),
                It.IsAny<MailAccount>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<IReadOnlyCollection<ProviderFeature>>()))
            .ReturnsAsync(new TokenInformationEx("token", account.Address));
        var service = CreateService(accountService, CreateFeatureService(account.Id), synchronizationManager, new WeakReferenceMessenger());

        await service.ReauthenticateAsync(account.Id);

        synchronizationManager.Verify(manager => manager.HandleAuthorizationAsync(
            MailProviderType.Outlook, account, false, true, It.IsAny<IReadOnlyCollection<ProviderFeature>>()), Times.Once);
        Assert.Equal(AccountAttentionReason.None, account.AttentionReason);
        Assert.True(account.IsContactAccessGranted);
        Assert.False(account.IsContactReauthorizationRequired);
    }

    [Theory]
    [InlineData(MailProviderType.Outlook, AccountAttentionReason.InvalidCredentials, false, AccountIntegrationSource.Provider, true)]
    [InlineData(MailProviderType.Gmail, AccountAttentionReason.None, true, AccountIntegrationSource.Provider, true)]
    [InlineData(MailProviderType.Gmail, AccountAttentionReason.None, false, AccountIntegrationSource.Provider, false)]
    [InlineData(MailProviderType.Gmail, AccountAttentionReason.None, true, AccountIntegrationSource.Local, false)]
    [InlineData(MailProviderType.Outlook, AccountAttentionReason.MissingSystemFolderConfiguration, true, AccountIntegrationSource.Provider, false)]
    [InlineData(MailProviderType.Outlook, AccountAttentionReason.CertificateValidationFailed, false, AccountIntegrationSource.Provider, false)]
    [InlineData(MailProviderType.IMAP4, AccountAttentionReason.InvalidCredentials, true, AccountIntegrationSource.Dav, false)]
    public void CanBeFixedBySigningIn_CoversExpiredCredentialsAndProviderReconsent(
        MailProviderType providerType,
        AccountAttentionReason attentionReason,
        bool contactsNeedConsent,
        AccountIntegrationSource contactSource,
        bool expected)
    {
        var account = new MailAccount
        {
            ProviderType = providerType,
            AttentionReason = attentionReason,
            IsContactAccessEnabled = true,
            ContactIntegrationSource = contactSource,
            IsContactReauthorizationRequired = contactsNeedConsent
        };

        Assert.Equal(expected, account.CanBeFixedBySigningIn());
    }

    [Fact]
    public async Task ReauthenticateAsync_LocalContactsWithStaleConsentFlag_NeverRequestsContactsScope()
    {
        var account = CreateRestoredAccount(MailProviderType.Gmail);
        account.AttentionReason = AccountAttentionReason.None;
        account.IsContactAccessEnabled = true;
        account.IsContactAccessGranted = true;
        account.IsContactReauthorizationRequired = true;
        account.ContactIntegrationSource = AccountIntegrationSource.Local;
        var accountService = CreateAccountService(account);
        var synchronizationManager = new Mock<ISynchronizationManager>();
        synchronizationManager
            .Setup(manager => manager.HandleAuthorizationAsync(
                MailProviderType.Gmail,
                account,
                true,
                true,
                It.IsAny<IReadOnlyCollection<ProviderFeature>>()))
            .Callback<MailProviderType, MailAccount, bool, bool, IReadOnlyCollection<ProviderFeature>>(
                (_, requestedAccount, _, _, _) =>
                    Assert.False(ProviderAuthorizationRequest.ForAccount(requestedAccount).IncludeContacts))
            .ReturnsAsync(new TokenInformationEx("token", account.Address));
        var service = CreateService(accountService, CreateFeatureService(account.Id), synchronizationManager, new WeakReferenceMessenger());

        // A stale flag on a local-backed mode is not a reason to sign in.
        Assert.False(account.CanBeFixedBySigningIn());

        await service.ReauthenticateAsync(account.Id);

        Assert.True(account.IsContactAccessEnabled);
        Assert.False(account.IsContactAccessGranted);
        Assert.False(account.IsContactReauthorizationRequired);
    }

    [Fact]
    public async Task ReauthenticateAsync_RejectsMailboxOwnedByAnotherAccount()
    {
        var account = CreateRestoredAccount(MailProviderType.Outlook);
        var accountService = CreateAccountService(account);
        accountService
            .Setup(service => service.AccountAddressExistsAsync("other@example.com", account.Id))
            .ReturnsAsync(true);
        var synchronizationManager = new Mock<ISynchronizationManager>();
        synchronizationManager
            .Setup(manager => manager.HandleAuthorizationAsync(
                It.IsAny<MailProviderType>(),
                It.IsAny<MailAccount>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<IReadOnlyCollection<ProviderFeature>>()))
            .ReturnsAsync(new TokenInformationEx("token", "other@example.com", "other@example.com"));
        var service = CreateService(accountService, CreateFeatureService(account.Id), synchronizationManager, new WeakReferenceMessenger());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReauthenticateAsync(account.Id));

        Assert.Equal(AccountAttentionReason.InvalidCredentials, account.AttentionReason);
        Assert.Equal("restored@example.com", account.Address);
        accountService.Verify(service => service.UpdateAccountAsync(It.IsAny<MailAccount>()), Times.Never);
    }

    [Fact]
    public async Task SynchronizeAfterReauthenticationAsync_RunsSetupStepsAndQueuesGrantedModes()
    {
        var account = CreateRestoredAccount(MailProviderType.Outlook);
        account.AttentionReason = AccountAttentionReason.None;
        account.IsCalendarAccessEnabled = true;
        account.IsCalendarAccessGranted = true;
        account.CalendarIntegrationSource = AccountIntegrationSource.Provider;
        account.IsContactAccessEnabled = true;
        account.IsContactAccessGranted = true;
        account.ContactIntegrationSource = AccountIntegrationSource.Provider;
        account.IsTaskAccessEnabled = false;
        account.IsTaskAccessGranted = false;
        var profile = new ProfileInformation("Restored User", ProfilePictureFetchResult.Downloaded([1, 2, 3]), account.Address);
        var accountService = CreateAccountService(account);
        accountService
            .Setup(service => service.GetAccountAliasesAsync(account.Id))
            .ReturnsAsync(new List<MailAccountAlias> { new() { AccountId = account.Id, IsRootAlias = true } });
        var synchronizationManager = new Mock<ISynchronizationManager>();
        synchronizationManager
            .Setup(manager => manager.SynchronizeProfileAsync(account.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MailSynchronizationResult.Completed(profile));
        synchronizationManager
            .Setup(manager => manager.SynchronizeFoldersAsync(account.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MailSynchronizationResult.Empty);
        synchronizationManager
            .Setup(manager => manager.SynchronizeCategoriesAsync(account.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MailSynchronizationResult.Empty);
        synchronizationManager
            .Setup(manager => manager.SynchronizeAliasesAsync(account.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MailSynchronizationResult.Empty);
        var messenger = new WeakReferenceMessenger();
        var recorder = new SynchronizationRequestRecorder(messenger);
        var service = CreateService(accountService, CreateFeatureService(account.Id), synchronizationManager, messenger);

        await service.SynchronizeAfterReauthenticationAsync(account.Id);

        accountService.Verify(service => service.UpdateProfileInformationAsync(account.Id, profile, false), Times.Once);
        synchronizationManager.Verify(manager => manager.SynchronizeFoldersAsync(account.Id, It.IsAny<CancellationToken>()), Times.Once);
        synchronizationManager.Verify(manager => manager.SynchronizeCategoriesAsync(account.Id, It.IsAny<CancellationToken>()), Times.Once);
        synchronizationManager.Verify(manager => manager.SynchronizeAliasesAsync(account.Id, It.IsAny<CancellationToken>()), Times.Once);
        accountService.Verify(service => service.CreateRootAliasAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);

        var mailRequest = Assert.Single(recorder.Mail);
        Assert.Equal(MailSynchronizationType.FullFolders, mailRequest.Options.Type);
        Assert.Equal(CalendarSynchronizationType.CalendarEvents, Assert.Single(recorder.Calendar).Options.Type);
        Assert.Equal(ContactSynchronizationType.Delta, Assert.Single(recorder.Contacts).Options.Type);
        Assert.Empty(recorder.Tasks);
    }

    [Fact]
    public async Task SynchronizeAfterReauthenticationAsync_ContinuesWhenProfileStepFails()
    {
        var account = CreateRestoredAccount(MailProviderType.Gmail);
        account.AttentionReason = AccountAttentionReason.None;
        var accountService = CreateAccountService(account);
        accountService
            .Setup(service => service.GetAccountAliasesAsync(account.Id))
            .ReturnsAsync(new List<MailAccountAlias>());
        var synchronizationManager = new Mock<ISynchronizationManager>();
        synchronizationManager
            .Setup(manager => manager.SynchronizeProfileAsync(account.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("profile endpoint unavailable"));
        synchronizationManager
            .Setup(manager => manager.SynchronizeFoldersAsync(account.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MailSynchronizationResult.Empty);
        synchronizationManager
            .Setup(manager => manager.SynchronizeAliasesAsync(account.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MailSynchronizationResult.Empty);
        var messenger = new WeakReferenceMessenger();
        var recorder = new SynchronizationRequestRecorder(messenger);
        var service = CreateService(accountService, CreateFeatureService(account.Id), synchronizationManager, messenger);

        await service.SynchronizeAfterReauthenticationAsync(account.Id);

        synchronizationManager.Verify(manager => manager.SynchronizeFoldersAsync(account.Id, It.IsAny<CancellationToken>()), Times.Once);
        synchronizationManager.Verify(manager => manager.SynchronizeCategoriesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        accountService.Verify(service => service.CreateRootAliasAsync(account.Id, account.Address), Times.Once);
        Assert.Single(recorder.Mail);
    }

    [Fact]
    public async Task SynchronizeAfterReauthenticationAsync_DoesNothingWhileAccountStillNeedsAttention()
    {
        var account = CreateRestoredAccount(MailProviderType.Outlook);
        var accountService = CreateAccountService(account);
        var synchronizationManager = new Mock<ISynchronizationManager>(MockBehavior.Strict);
        var messenger = new WeakReferenceMessenger();
        var recorder = new SynchronizationRequestRecorder(messenger);
        var service = CreateService(accountService, CreateFeatureService(account.Id), synchronizationManager, messenger);

        await service.SynchronizeAfterReauthenticationAsync(account.Id);

        Assert.Empty(recorder.Mail);
        Assert.Empty(recorder.Calendar);
    }

    private static AccountReauthenticationService CreateService(
        Mock<IAccountService> accountService,
        Mock<IAccountProviderFeatureService> featureService,
        Mock<ISynchronizationManager> synchronizationManager,
        IMessenger messenger)
        => new(accountService.Object, featureService.Object, synchronizationManager.Object, messenger);

    private static Mock<IAccountService> CreateAccountService(MailAccount account)
    {
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountAsync(account.Id)).ReturnsAsync(account);
        accountService.Setup(service => service.UpdateAccountAsync(account)).Returns(Task.CompletedTask);
        return accountService;
    }

    private static Mock<IAccountProviderFeatureService> CreateFeatureService(Guid accountId)
    {
        var featureService = new Mock<IAccountProviderFeatureService>();
        featureService
            .Setup(service => service.GetFeaturesAsync(accountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AccountProviderFeature>());
        return featureService;
    }

    private static MailAccount CreateRestoredAccount(MailProviderType providerType)
        => new()
        {
            Id = Guid.NewGuid(),
            Address = "restored@example.com",
            Name = "Restored",
            SenderName = "restored@example.com",
            ProviderType = providerType,
            IsMailAccessGranted = true,
            IsCalendarAccessEnabled = false,
            IsCalendarAccessGranted = false,
            IsContactAccessEnabled = false,
            IsTaskAccessEnabled = false,
            AttentionReason = AccountAttentionReason.InvalidCredentials
        };

    private sealed class SynchronizationRequestRecorder
    {
        public List<NewMailSynchronizationRequested> Mail { get; } = [];
        public List<NewCalendarSynchronizationRequested> Calendar { get; } = [];
        public List<NewContactSynchronizationRequested> Contacts { get; } = [];
        public List<NewTaskSynchronizationRequested> Tasks { get; } = [];

        public SynchronizationRequestRecorder(IMessenger messenger)
        {
            messenger.Register<SynchronizationRequestRecorder, NewMailSynchronizationRequested>(this, (recipient, message) => recipient.Mail.Add(message));
            messenger.Register<SynchronizationRequestRecorder, NewCalendarSynchronizationRequested>(this, (recipient, message) => recipient.Calendar.Add(message));
            messenger.Register<SynchronizationRequestRecorder, NewContactSynchronizationRequested>(this, (recipient, message) => recipient.Contacts.Add(message));
            messenger.Register<SynchronizationRequestRecorder, NewTaskSynchronizationRequested>(this, (recipient, message) => recipient.Tasks.Add(message));
        }
    }
}
