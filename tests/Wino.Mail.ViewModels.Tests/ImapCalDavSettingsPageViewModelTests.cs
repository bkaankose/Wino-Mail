using System.Reflection;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Data;
using Wino.Services;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class ImapCalDavSettingsPageViewModelTests
{
    [Theory]
    [InlineData(SpecialImapProvider.iCloud, "person@icloud.com", "imap.mail.me.com", "person", "smtp.mail.me.com", "person@icloud.com")]
    [InlineData(SpecialImapProvider.Yahoo, "person@yahoo.com", "imap.mail.yahoo.com", "person@yahoo.com", "smtp.mail.yahoo.com", "person@yahoo.com")]
    public void CreateMode_LoadsKnownProviderSettingsFromCatalog(
        SpecialImapProvider provider,
        string address,
        string incomingHost,
        string incomingUsername,
        string outgoingHost,
        string outgoingUsername)
    {
        var viewModel = CreateViewModel();
        var result = CreateDialogResult(provider, address);

        viewModel.OnNavigatedTo(
            NavigationMode.New,
            ImapCalDavSettingsNavigationContext.CreateForWizardMode(result));

        viewModel.IncomingServer.Should().Be(incomingHost);
        viewModel.IncomingServerUsername.Should().Be(incomingUsername);
        viewModel.OutgoingServer.Should().Be(outgoingHost);
        viewModel.OutgoingServerUsername.Should().Be(outgoingUsername);
    }

    [Fact]
    public void EditMode_DoesNotReapplyCatalogOverStoredSettings()
    {
        var accountId = Guid.NewGuid();
        var storedServer = new CustomServerInformation
        {
            IncomingServer = "custom.imap.example",
            IncomingServerPort = "1993",
            IncomingServerUsername = "stored-incoming",
            IncomingServerPassword = "stored-password",
            OutgoingServer = "custom.smtp.example",
            OutgoingServerPort = "1587",
            OutgoingServerUsername = "stored-outgoing",
            OutgoingServerPassword = "stored-password",
            MaxConcurrentClients = 3,
            CalendarSupportMode = ImapCalendarSupportMode.Disabled
        };
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountAsync(accountId)).ReturnsAsync(new MailAccount
        {
            Id = accountId,
            ProviderType = MailProviderType.IMAP4,
            SpecialImapProvider = SpecialImapProvider.iCloud,
            Address = "person@icloud.com",
            SenderName = "Person",
            IsMailAccessGranted = true,
            ServerInformation = storedServer
        });
        var viewModel = CreateViewModel(accountService.Object);

        viewModel.OnNavigatedTo(NavigationMode.New, ImapCalDavSettingsNavigationContext.CreateForEditMode(accountId));
        var method = typeof(ImapCalDavSettingsPageViewModel).GetMethod(
            "TryApplyKnownProviderSettings",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var applied = (bool)method.Invoke(viewModel, [true])!;

        applied.Should().BeFalse();
        viewModel.IncomingServer.Should().Be("custom.imap.example");
        viewModel.IncomingServerPort.Should().Be("1993");
        viewModel.OutgoingServer.Should().Be("custom.smtp.example");
        viewModel.OutgoingServerPort.Should().Be("1587");
    }

    private static ImapCalDavSettingsPageViewModel CreateViewModel(IAccountService accountService = null)
    {
        var catalog = new EmbeddedKnownImapProviderCatalog(new KnownImapProviderCatalogLoader());
        return new ImapCalDavSettingsPageViewModel(
            Mock.Of<IAutoDiscoveryService>(),
            Mock.Of<ICalDavClient>(),
            accountService ?? Mock.Of<IAccountService>(),
            Mock.Of<IMailDialogService>(),
            new SpecialImapProviderConfigResolver(catalog),
            Mock.Of<IWinoTelemetryService>(),
            new WelcomeWizardContext());
    }

    private static AccountCreationDialogResult CreateDialogResult(SpecialImapProvider provider, string address)
        => new(
            MailProviderType.IMAP4,
            provider.ToString(),
            new SpecialImapProviderDetails(address, "app-password", "Person", provider, ImapCalendarSupportMode.CalDav),
            "#0078D4",
            InitialSynchronizationRange.SixMonths,
            true,
            true);
}
