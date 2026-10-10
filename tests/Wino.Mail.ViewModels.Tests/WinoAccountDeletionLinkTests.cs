using Moq;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;
using Wino.Core.ViewModels;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

/// <summary>
/// The Delete Wino Account card opens the website's account deletion page.
/// </summary>
public sealed class WinoAccountDeletionLinkTests
{
    [Fact]
    public async Task OpenAccountDeletion_LaunchesDeletionPage()
    {
        var launcher = new Mock<IExternalLauncher>();
        launcher.Setup(l => l.LaunchUriAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformOperationResult(PlatformOperationStatus.Succeeded));
        var vm = new WinoAccountManagementPageViewModel(Mock.Of<IWinoAccountProfileService>(), Mock.Of<IMailDialogService>(),
            Mock.Of<IWinoBillingService>(), Mock.Of<IWinoAccountApiClient>(), Mock.Of<IAccountService>(),
            Mock.Of<IMailIntelligenceCoordinator>(), Mock.Of<IPreferencesService>(), new PlatformCapabilities(), launcher.Object);

        await vm.OpenAccountDeletionCommand.ExecuteAsync(null);

        launcher.Verify(l => l.LaunchUriAsync(new Uri("https://www.winomail.app/account/delete"), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("https://www.winomail.app/account/delete", AppUrls.WinoAccountDeletion);
    }
}
