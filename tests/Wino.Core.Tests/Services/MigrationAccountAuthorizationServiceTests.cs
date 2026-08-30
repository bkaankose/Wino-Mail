using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;
using Wino.Core.Domain.Models.Migration;
using Wino.Core.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class MigrationAccountAuthorizationServiceTests
{
    [Fact]
    public async Task AuthenticateAsync_RequestsSelectedScopesAndActivatesFeaturesWithoutSynchronizing()
    {
        var account = CreateAccount();
        var options = CreateOptions(account.Id);
        IReadOnlyCollection<ProviderFeature> requestedFeatures = null;
        var authenticator = new Mock<IAuthenticator>();
        authenticator
            .Setup(service => service.GenerateTokenInformationAsync(
                It.IsAny<MailAccount>(),
                It.IsAny<IReadOnlyCollection<ProviderFeature>>()))
            .Callback<MailAccount, IReadOnlyCollection<ProviderFeature>>((requestedAccount, features) =>
            {
                Assert.True(requestedAccount.IsContactAccessGranted);
                Assert.True(requestedAccount.IsTaskAccessGranted);
                requestedFeatures = features;
            })
            .ReturnsAsync(new TokenInformationEx("token", "updated@example.com", "login@example.com"));
        var authenticationProvider = new Mock<IAuthenticationProvider>();
        authenticationProvider
            .Setup(service => service.GetAuthenticator(MailProviderType.Outlook))
            .Returns(authenticator.Object);
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountAsync(account.Id)).ReturnsAsync(account);
        accountService.Setup(service => service.UpdateAccountAsync(account)).Returns(Task.CompletedTask);
        var featureService = new Mock<IAccountProviderFeatureService>();
        featureService
            .Setup(service => service.GetFeatureAsync(account.Id, ProviderFeature.MailFilters, It.IsAny<CancellationToken>()))
            .ReturnsAsync((AccountProviderFeature)null);
        AccountProviderFeature savedFeature = null;
        featureService
            .Setup(service => service.UpsertAsync(It.IsAny<AccountProviderFeature>(), It.IsAny<CancellationToken>()))
            .Callback<AccountProviderFeature, CancellationToken>((feature, _) => savedFeature = feature)
            .Returns(Task.CompletedTask);
        var service = new MigrationAccountAuthorizationService(
            accountService.Object,
            featureService.Object,
            authenticationProvider.Object);

        await service.AuthenticateAsync(options);

        Assert.Contains(ProviderFeature.MailFilters, requestedFeatures);
        Assert.True(account.IsContactAccessGranted);
        Assert.False(account.IsContactReauthorizationRequired);
        Assert.True(account.IsTaskAccessGranted);
        Assert.False(account.IsTaskReauthorizationRequired);
        Assert.Equal(AccountAttentionReason.None, account.AttentionReason);
        Assert.Equal("updated@example.com", account.Address);
        Assert.Equal("login@example.com", account.AuthenticationAddress);
        Assert.Equal(ProviderFeatureAuthorizationState.Active, savedFeature.AuthorizationState);
    }

    [Fact]
    public async Task SkipAsync_MarksAccountAttentionAndLeavesEverySelectedFeatureUnauthorized()
    {
        var account = CreateAccount();
        var options = CreateOptions(account.Id);
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountAsync(account.Id)).ReturnsAsync(account);
        accountService.Setup(service => service.UpdateAccountAsync(account)).Returns(Task.CompletedTask);
        var featureService = new Mock<IAccountProviderFeatureService>();
        featureService
            .Setup(service => service.GetFeatureAsync(account.Id, ProviderFeature.MailFilters, It.IsAny<CancellationToken>()))
            .ReturnsAsync((AccountProviderFeature)null);
        AccountProviderFeature savedFeature = null;
        featureService
            .Setup(service => service.UpsertAsync(It.IsAny<AccountProviderFeature>(), It.IsAny<CancellationToken>()))
            .Callback<AccountProviderFeature, CancellationToken>((feature, _) => savedFeature = feature)
            .Returns(Task.CompletedTask);
        var service = new MigrationAccountAuthorizationService(
            accountService.Object,
            featureService.Object,
            Mock.Of<IAuthenticationProvider>());

        await service.SkipAsync(options);

        Assert.Equal(AccountAttentionReason.InvalidCredentials, account.AttentionReason);
        Assert.False(account.IsContactAccessGranted);
        Assert.True(account.IsContactReauthorizationRequired);
        Assert.False(account.IsTaskAccessGranted);
        Assert.True(account.IsTaskReauthorizationRequired);
        Assert.Equal(ProviderFeatureAuthorizationState.ReauthorizationRequired, savedFeature.AuthorizationState);
    }

    private static MailAccount CreateAccount() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Work",
        Address = "work@example.com",
        ProviderType = MailProviderType.Outlook,
        IsMailAccessGranted = true,
        IsContactAccessEnabled = true,
        IsContactReauthorizationRequired = true,
        IsTaskAccessEnabled = true,
        IsTaskReauthorizationRequired = true,
        AttentionReason = AccountAttentionReason.InvalidCredentials
    };

    private static MigrationAccountOptions CreateOptions(Guid accountId) => new(
        accountId,
        "Work",
        "work@example.com",
        MailProviderType.Outlook,
        EnableContacts: true,
        EnableTasks: true,
        EnableMailFilters: true,
        DeferSignIn: true);
}
