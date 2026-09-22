using System.Reflection;
using FluentAssertions;
using Moq;
using Wino.Core.Domain;
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

    [Fact]
    public async Task CalendarOnlyWizard_SignsInWithUserNameAndDiscoversCalDav()
    {
        var autoDiscovery = new Mock<IAutoDiscoveryService>();
        autoDiscovery
            .Setup(service => service.DiscoverCalDavServiceUriAsync("alex", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Uri("https://dav.example.org/"));
        var viewModel = CreateViewModel(autoDiscoveryService: autoDiscovery.Object);
        var result = new AccountCreationDialogResult(
            MailProviderType.IMAP4,
            "Family calendar",
            null,
            "#0078D4",
            InitialSynchronizationRange.SixMonths,
            IsMailAccessGranted: false,
            IsCalendarAccessGranted: true,
            CalendarSupportMode: ImapCalendarSupportMode.CalDav);

        viewModel.OnNavigatedTo(NavigationMode.New, ImapCalDavSettingsNavigationContext.CreateForWizardMode(result));

        viewModel.Capabilities.MailMode.Should().Be(AccountCapabilityMode.Off);
        viewModel.Capabilities.CalendarMode.Should().Be(AccountCapabilityMode.Provider);
        viewModel.IsSignInStepVisible.Should().BeTrue();
        viewModel.IsSignInSenderNameVisible.Should().BeFalse();
        viewModel.IsSignInServerAddressVisible.Should().BeTrue();

        viewModel.EmailAddress = "alex";
        viewModel.Password = "secret";
        await viewModel.PrimaryActionCommand.ExecuteAsync(null);

        viewModel.IsPageInfoBarOpen.Should().BeFalse(viewModel.PageInfoBarMessage);
        viewModel.CurrentSetupStep.Should().Be(ImapSetupStep.Servers);
        viewModel.DiscoveryState.Should().Be(ServerDiscoveryState.Found);
        viewModel.CalDavServiceUrl.Should().Be("https://dav.example.org/");
        viewModel.IsServersStepVisible.Should().BeTrue();
        viewModel.PrimaryActionText.Should().Be(Translator.ProviderSelection_AddAccountButton);
    }

    [Fact]
    public async Task SignIn_KeepsTheUserOnTheStepWhenThePasswordIsMissing()
    {
        var viewModel = CreateViewModel();
        viewModel.OnNavigatedTo(NavigationMode.New, ImapCalDavSettingsNavigationContext.CreateForWizardMode(CreateGenericMailResult()));

        viewModel.DisplayName = "Alex";
        viewModel.EmailAddress = "alex@example.org";
        viewModel.Password = string.Empty;
        await viewModel.PrimaryActionCommand.ExecuteAsync(null);

        viewModel.CurrentSetupStep.Should().Be(ImapSetupStep.SignIn);
        viewModel.IsPageInfoBarOpen.Should().BeTrue();
    }

    [Fact]
    public void SharedOutgoingSignIn_UsesTheIncomingCredentials()
    {
        var viewModel = CreateViewModel();
        viewModel.OnNavigatedTo(NavigationMode.New, ImapCalDavSettingsNavigationContext.CreateForWizardMode(CreateGenericMailResult()));

        viewModel.IncomingServerUsername = "incoming-user";
        viewModel.IncomingServerPassword = "incoming-password";
        viewModel.OutgoingServerUsername = "stale-user";
        viewModel.OutgoingServerPassword = "stale-password";
        viewModel.UseIncomingCredentialsForOutgoing = true;

        var serverInformation = BuildServerInformation(viewModel);
        serverInformation.OutgoingServerUsername.Should().Be("incoming-user");
        serverInformation.OutgoingServerPassword.Should().Be("incoming-password");

        viewModel.UseIncomingCredentialsForOutgoing = false;

        serverInformation = BuildServerInformation(viewModel);
        serverInformation.OutgoingServerUsername.Should().Be("stale-user");
        viewModel.IsOutgoingCredentialsVisible.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(7.4, 7)]
    [InlineData(80, 20)]
    [InlineData(double.NaN, 5)]
    public void ConnectionLimit_IsAWholeNumberWithinRange(double value, int expected)
    {
        var viewModel = CreateViewModel();

        viewModel.MaxConcurrentClientsValue = value;

        viewModel.MaxConcurrentClients.Should().Be(expected);
    }

    [Fact]
    public void AppPasswordHint_FollowsTheAddressDomain()
    {
        var viewModel = CreateViewModel();

        viewModel.EmailAddress = "alex@fastmail.com";
        viewModel.AppPasswordHelpUrl.Should().Contain("fastmail.help");
        viewModel.HasAppPasswordHelpLink.Should().BeTrue();

        viewModel.EmailAddress = "alex@example.org";
        viewModel.AppPasswordHelpUrl.Should().BeEmpty();
        viewModel.AppPasswordHelpText.Should().Be(Translator.ImapSetup_AppPasswordGenericHint);
    }

    private static CustomServerInformation BuildServerInformation(ImapCalDavSettingsPageViewModel viewModel)
        => (CustomServerInformation)typeof(ImapCalDavSettingsPageViewModel)
            .GetMethod("BuildServerInformation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, [])!;

    private static AccountCreationDialogResult CreateGenericMailResult()
        => new(
            MailProviderType.IMAP4,
            "Work",
            null,
            "#0078D4",
            InitialSynchronizationRange.SixMonths,
            true,
            false);

    private static ImapCalDavSettingsPageViewModel CreateViewModel(
        IAccountService accountService = null,
        IAutoDiscoveryService autoDiscoveryService = null)
    {
        var catalog = new EmbeddedKnownImapProviderCatalog(new KnownImapProviderCatalogLoader());
        return new ImapCalDavSettingsPageViewModel(
            autoDiscoveryService ?? Mock.Of<IAutoDiscoveryService>(),
            Mock.Of<ICalDavClient>(),
            accountService ?? Mock.Of<IAccountService>(),
            Mock.Of<IMailDialogService>(),
            new SpecialImapProviderConfigResolver(catalog),
            Mock.Of<IWinoTelemetryService>(),
            new WelcomeWizardContext(),
            knownImapProviderCatalog: catalog);
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
