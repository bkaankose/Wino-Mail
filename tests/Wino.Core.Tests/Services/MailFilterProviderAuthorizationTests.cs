using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class MailFilterProviderAuthorizationTests
{
    [Fact]
    public async Task GetFiltersAsync_WithoutOptIn_DoesNotCreateOrCallSynchronizer()
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            ProviderType = MailProviderType.Gmail
        };
        var synchronizerFactory = new Mock<ISynchronizerFactory>(MockBehavior.Strict);
        var featureStore = new Mock<IAccountProviderFeatureService>();
        featureStore
            .Setup(service => service.IsEnabledAsync(account.Id, ProviderFeature.MailFilters, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var service = new MailFilterProviderService(
            synchronizerFactory.Object,
            Mock.Of<IMailFilterService>(),
            featureStore.Object,
            Mock.Of<IProviderFeatureAuthorizationService>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetFiltersAsync(account));
        synchronizerFactory.Verify(
            factory => factory.GetAccountSynchronizerAsync(It.IsAny<Guid>()),
            Times.Never);
    }
}
