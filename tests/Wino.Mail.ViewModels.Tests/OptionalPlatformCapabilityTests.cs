using System.Reflection;
using Moq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class OptionalPlatformCapabilityTests
{
    [Theory]
    [InlineData(MailOperation.Print)]
    [InlineData(MailOperation.SaveAsPdf)]
    [InlineData(MailOperation.SaveAs)]
    public async Task UnavailableReaderOperationsDoNotEnterPresentationOrFilePicking(MailOperation operation)
    {
        var dialogs = new Mock<IMailDialogService>(MockBehavior.Strict);
        var files = new Mock<IFileService>(MockBehavior.Strict);
        var presenter = new Mock<IMailPrintPresenter>(MockBehavior.Strict);
        var vm = new MailRenderingPageViewModel(dialogs.Object, Mock.Of<IExternalLauncher>(),
            Mock.Of<IClipboardService>(), Mock.Of<IUnderlyingThemeService>(), Mock.Of<IMimeFileService>(),
            Mock.Of<IMailService>(), Mock.Of<IFolderService>(), files.Object, Mock.Of<IWinoRequestDelegator>(),
            Mock.Of<IStatePersistanceService>(), Mock.Of<IContactService>(), Mock.Of<IUnsubscriptionService>(),
            Mock.Of<IPreferencesService>(), new PlatformCapabilities(), Mock.Of<IApplicationConfiguration>())
        { PrintPresenter = presenter.Object };

        var handler = typeof(MailRenderingPageViewModel).GetMethod("HandleMailOperationAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)handler.Invoke(vm, new object[] { operation })!;

        presenter.VerifyNoOtherCalls();
        files.VerifyNoOtherCalls();
        dialogs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnavailableStartupRejectsDirectCommandExecutionWithoutCallingPlatform()
    {
        var startup = new Mock<IStartupIntegrationService>(MockBehavior.Strict);
        var dialogs = new Mock<IMailDialogService>(MockBehavior.Strict);
        var vm = new AppPreferencesPageViewModel(dialogs.Object, Mock.Of<IPreferencesService>(),
            startup.Object, new PlatformCapabilities(), Mock.Of<ITranslationService>());

        Assert.False(vm.ToggleStartupBehaviorCommand.CanExecute(null));
        await vm.ToggleStartupBehaviorCommand.ExecuteAsync(null);

        startup.VerifyNoOtherCalls();
        dialogs.VerifyNoOtherCalls();
    }

    [Fact]
    public void UnavailableTrayDoesNotOverwritePersistedClosePreference()
    {
        var preferences = new Mock<IPreferencesService>();
        preferences.SetupProperty(p => p.AppCloseBehavior, AppCloseBehavior.RunInBackgroundWithTrayIcon);
        var vm = new AppPreferencesPageViewModel(Mock.Of<IMailDialogService>(), preferences.Object,
            Mock.Of<IStartupIntegrationService>(), new PlatformCapabilities(), Mock.Of<ITranslationService>());

        Assert.Equal(AppCloseBehavior.RunInBackgroundWithTrayIcon, preferences.Object.AppCloseBehavior);
        vm.SelectedCloseBehaviorMode = vm.CloseBehaviorModes[(int)AppCloseBehavior.RunInBackgroundWithTrayIcon];
        Assert.Equal(AppCloseBehavior.RunInBackgroundWithTrayIcon, preferences.Object.AppCloseBehavior);
        preferences.VerifySet(p => p.AppCloseBehavior = It.IsAny<AppCloseBehavior>(), Times.Never);
    }
}
