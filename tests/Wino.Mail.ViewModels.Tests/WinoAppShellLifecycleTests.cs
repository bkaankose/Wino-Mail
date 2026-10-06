using System.ComponentModel;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Navigation;
using Wino.Shell.ViewModels;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class WinoAppShellLifecycleTests
{
    [Fact]
    public void GetProvider_ResolvesOnlyVisitedModesAndCachesTheProvider()
    {
        var resolver = new Mock<IShellMenuProviderResolver>(MockBehavior.Strict);
        var provider = new Mock<IShellMenuProvider>();
        resolver.Setup(x => x.Resolve(WinoApplicationMode.Mail)).Returns(provider.Object);
        var shell = CreateShell(resolver);

        resolver.VerifyNoOtherCalls();
        shell.GetProvider(WinoApplicationMode.Mail).Should().BeSameAs(provider.Object);
        shell.GetProvider(WinoApplicationMode.Mail).Should().BeSameAs(provider.Object);

        resolver.Verify(x => x.Resolve(WinoApplicationMode.Mail), Times.Once);
        resolver.VerifyNoOtherCalls();
        shell.ShutdownProviders();
    }

    [Fact]
    public void ReplacingProvider_DetachesOldEventsAndAcceptsRebuiltMenu()
    {
        var shell = CreateShell(new Mock<IShellMenuProviderResolver>());
        var first = new Mock<IShellMenuProvider>();
        var second = new Mock<IShellMenuProvider>();
        var menu = CreateMenu();
        second.SetupGet(x => x.ShellMenu).Returns(menu);
        shell.SetPaneCompact(true);
        shell.SetShellMenu(first.Object);
        shell.SetShellMenu(second.Object);
        var changes = 0;
        shell.PropertyChanged += (_, _) => changes++;

        first.Raise(x => x.PropertyChanged += null, new PropertyChangedEventArgs(nameof(IShellMenuProvider.SelectedMenuItem)));
        changes.Should().Be(0);
        second.Verify(x => x.SetPaneCompact(true), Times.Once);
        shell.CurrentMenu.Should().BeSameAs(menu);

        var rebuilt = CreateMenu();
        second.SetupGet(x => x.ShellMenu).Returns(rebuilt);
        second.Raise(x => x.PropertyChanged += null, new PropertyChangedEventArgs(nameof(IShellMenuProvider.ShellMenu)));
        shell.CurrentMenu.Should().BeSameAs(rebuilt);
        shell.ShutdownProviders();
    }

    [Fact]
    public void SelectionFeedback_DoesNotReenterProviderSetter()
    {
        var shell = CreateShell(new Mock<IShellMenuProviderResolver>());
        var provider = new Mock<IShellMenuProvider>();
        var selection = new object();
        provider.SetupSet(x => x.SelectedMenuItem = selection)
            .Callback(() => shell.SelectedMenuItem = selection);
        shell.SetShellMenu(provider.Object);

        shell.SelectedMenuItem = selection;

        provider.VerifySet(x => x.SelectedMenuItem = selection, Times.Once);
        shell.ShutdownProviders();
    }

    [Fact]
    public void Shutdown_IsIdempotentReleasesDispatcherAndDetachesStateSubscription()
    {
        var resolver = new Mock<IShellMenuProviderResolver>();
        var provider = new Mock<IShellMenuProvider>();
        provider.SetupProperty(x => x.Dispatcher, Mock.Of<IDispatcher>());
        resolver.Setup(x => x.Resolve(WinoApplicationMode.Mail)).Returns(provider.Object);
        var state = new Mock<IStatePersistanceService>();
        var shell = CreateShell(resolver, state);
        shell.GetProvider(WinoApplicationMode.Mail);
        shell.SetShellMenu(provider.Object);

        shell.ShutdownProviders();
        shell.ShutdownProviders();
        state.SetupGet(x => x.ApplicationMode).Returns(WinoApplicationMode.Calendar);
        state.Raise(x => x.StatePropertyChanged += null, state.Object, nameof(IStatePersistanceService.ApplicationMode));

        provider.Verify(x => x.ReleaseShellMenu(), Times.Once);
        provider.Object.Dispatcher.Should().BeNull();
        shell.CurrentProvider.Should().BeNull();
        shell.TryGetProvider(WinoApplicationMode.Mail, out _).Should().BeFalse();
        shell.CurrentMode.Should().NotBe(WinoApplicationMode.Calendar);
    }

    private static WinoAppShellViewModel CreateShell(
        Mock<IShellMenuProviderResolver> resolver,
        Mock<IStatePersistanceService>? state = null)
        => new(resolver.Object,
            Mock.Of<IPreferencesService>(),
            (state ?? new Mock<IStatePersistanceService>()).Object,
            Mock.Of<INavigationService>(),
            Mock.Of<IMicrosoftStoreService>(),
            Mock.Of<IMailDialogService>(),
            Mock.Of<IWinoLogger>(),
            Mock.Of<IPlatformCapabilities>());

    private static ShellMenu CreateMenu()
        => new() { Items = new MenuItemCollection(Mock.Of<IDispatcher>()) };
}
