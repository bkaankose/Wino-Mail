using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;
using Wino.Core.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class ProviderFeatureAuthorizationServiceTests
{
    [Fact]
    public async Task EnableAsync_VerifiesPermissionBeforePersisting_AndPreservesDeltaTokens()
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Address = "user@example.com",
            ProviderType = MailProviderType.Outlook,
            IsMailAccessGranted = true,
            SynchronizationDeltaIdentifier = "mail-delta",
            CalendarSynchronizationDeltaIdentifier = "calendar-delta"
        };
        var featureStore = new Mock<IAccountProviderFeatureService>();
        featureStore
            .Setup(service => service.GetFeaturesAsync(account.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        AccountProviderFeature persistedFeature = null;
        featureStore
            .Setup(service => service.UpsertAsync(It.IsAny<AccountProviderFeature>(), It.IsAny<CancellationToken>()))
            .Callback<AccountProviderFeature, CancellationToken>((feature, _) => persistedFeature = feature)
            .Returns(Task.CompletedTask);

        IReadOnlyCollection<ProviderFeature> requestedFeatures = null;
        var authenticator = new Mock<IAuthenticator>();
        authenticator.SetupGet(item => item.ProviderType).Returns(MailProviderType.Outlook);
        authenticator
            .Setup(item => item.GenerateTokenInformationAsync(account, It.IsAny<IReadOnlyCollection<ProviderFeature>>()))
            .Callback<MailAccount, IReadOnlyCollection<ProviderFeature>>((_, features) => requestedFeatures = features)
            .ReturnsAsync(new TokenInformationEx("token", account.Address));
        var authenticationProvider = new Mock<IAuthenticationProvider>();
        authenticationProvider
            .Setup(provider => provider.GetAuthenticator(MailProviderType.Outlook))
            .Returns(authenticator.Object);

        var synchronizer = new Mock<IWinoSynchronizerBase>();
        var filterSynchronizer = synchronizer.As<IProviderMailFilterSynchronizer>();
        filterSynchronizer
            .Setup(item => item.GetProviderFiltersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MailFilter>());
        var synchronizerFactory = new Mock<ISynchronizerFactory>();
        synchronizerFactory
            .Setup(factory => factory.GetAccountSynchronizerAsync(account.Id))
            .ReturnsAsync(synchronizer.Object);

        MailAccount updatedAccount = null;
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountAsync(account.Id)).ReturnsAsync(account);
        accountService
            .Setup(service => service.UpdateAccountAsync(account))
            .Callback<MailAccount>(value => updatedAccount = value)
            .Returns(Task.CompletedTask);
        var mailFilterService = new Mock<IMailFilterService>();
        mailFilterService
            .Setup(service => service.ReplaceProviderFiltersAsync(
                account.Id,
                It.IsAny<IReadOnlyCollection<MailFilter>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new ProviderFeatureAuthorizationService(
            featureStore.Object,
            accountService.Object,
            authenticationProvider.Object,
            synchronizerFactory.Object,
            mailFilterService.Object);

        await service.EnableAsync(account.Id, ProviderFeature.MailFilters);

        Assert.Contains(ProviderFeature.MailFilters, requestedFeatures);
        Assert.Equal(ProviderFeatureAuthorizationState.Active, persistedFeature.AuthorizationState);
        Assert.Equal("mail-delta", updatedAccount.SynchronizationDeltaIdentifier);
        Assert.Equal("calendar-delta", updatedAccount.CalendarSynchronizationDeltaIdentifier);
        filterSynchronizer.Verify(
            item => item.GetProviderFiltersAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
