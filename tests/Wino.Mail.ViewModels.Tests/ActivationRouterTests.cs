using FluentAssertions;
using Moq;
using Wino.Core.Activation;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Launch;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Activation;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class ActivationRouterTests : IDisposable
{
    private readonly Mock<IApplicationShellHost> _host = new();
    private readonly Mock<IAccountService> _accounts = new();
    private readonly Mock<IMailDialogService> _dialogs = new();
    private readonly Mock<IActivationStateService> _state = new();
    private readonly Mock<IActivationFileImportService> _importer = new();
    private readonly Mock<IDispatcher> _dispatcher = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "wino-activation-" + Guid.NewGuid().ToString("N"));

    public ActivationRouterTests()
    {
        Directory.CreateDirectory(_folder);
        _host.Setup(h => h.EnsureShellAsync(It.IsAny<WinoApplicationMode>(), It.IsAny<object?>())).ReturnsAsync(true);
        _state.SetupProperty(s => s.MailToUri);
        _state.SetupProperty(s => s.PendingShareRequest);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private ActivationRouter CreateRouter() => new(
        Mock.Of<IServiceProvider>(), _host.Object, _accounts.Object, _dialogs.Object, _state.Object, _importer.Object, _dispatcher.Object);

    private void HasAccounts(bool any)
        => _accounts.Setup(a => a.GetAccountsAsync()).ReturnsAsync(any ? [new MailAccount { Id = Guid.NewGuid() }] : []);

    private string WriteFile(string name, string content = "data")
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class TestFile(string? path) : IActivationFile
    {
        public int Opened { get; private set; }
        public int Closed { get; private set; }
        public string? Path => path;

        public IDisposable BeginAccess()
        {
            Opened++;
            return new Scope(this);
        }

        private sealed class Scope(TestFile file) : IDisposable
        {
            public void Dispose() => file.Closed++;
        }
    }

    [Fact]
    public void Group_SplitsUrlsAndFilesByKind()
    {
        var calendar = new TestFile("a.ics");
        var contact = new TestFile("b.vcf");
        var mail = new TestFile("c.eml");
        var other = new TestFile("d.pdf");
        var items = new ActivationItem[]
        {
            new(ActivationUriKind.Webcal, "webcal://example.com/cal.ics"),
            new(ActivationUriKind.CalendarFile, "file:///a.ics", calendar),
            new(ActivationUriKind.MailTo, "mailto:someone@example.com"),
            new(ActivationUriKind.ContactFile, "file:///b.vcf", contact),
            new(ActivationUriKind.MailFile, "file:///c.eml", mail),
            new(ActivationUriKind.Unsupported, "file:///d.pdf", other),
            new(ActivationUriKind.Unsupported, "ftp://example.com"),
            new(ActivationUriKind.BillingSuccess, "wino://billing/success")
        };

        var groups = ActivationRouter.Group(items);

        groups.Urls.Select(u => u.Kind).Should().Equal(ActivationUriKind.Webcal, ActivationUriKind.MailTo, ActivationUriKind.BillingSuccess);
        groups.CalendarFiles.Should().ContainSingle().Which.Should().BeSameAs(calendar);
        groups.ContactFiles.Should().ContainSingle().Which.Should().BeSameAs(contact);
        groups.MailFiles.Should().ContainSingle().Which.Should().BeSameAs(mail);
        groups.AttachmentFiles.Should().ContainSingle().Which.Should().BeSameAs(other);
        groups.Ignored.Should().ContainSingle().Which.Value.Should().Be("ftp://example.com");
    }

    [Fact]
    public async Task MailTo_WithoutAccounts_ShowsAMessageAndStops()
    {
        HasAccounts(false);

        await CreateRouter().HandleAsync([new ActivationItem(ActivationUriKind.MailTo, "mailto:someone@example.com")]);

        _dialogs.Verify(d => d.ShowMessageAsync(It.IsAny<string>(), It.IsAny<string>(), WinoCustomMessageDialogIcon.Warning), Times.Once);
        _state.Object.MailToUri.Should().BeNull();
        _host.Verify(h => h.EnsureShellAsync(It.IsAny<WinoApplicationMode>(), It.IsAny<object?>()), Times.Never);
    }

    [Fact]
    public async Task MailTo_HandsTheLinkToTheMailShell()
    {
        HasAccounts(true);

        await CreateRouter().HandleAsync([new ActivationItem(ActivationUriKind.MailTo, "mailto:someone@example.com?subject=Hi")]);

        _state.Object.MailToUri.Should().NotBeNull();
        _host.Verify(h => h.EnsureShellAsync(WinoApplicationMode.Mail, null), Times.Once);
    }

    [Fact]
    public async Task MalformedMailTo_IsIgnored()
    {
        HasAccounts(true);

        await CreateRouter().HandleAsync([new ActivationItem(ActivationUriKind.MailTo, "not a mailto")]);

        _host.Verify(h => h.EnsureShellAsync(It.IsAny<WinoApplicationMode>(), It.IsAny<object?>()), Times.Never);
    }

    [Fact]
    public async Task BillingSuccess_OpensWinoAccountSettings()
    {
        await CreateRouter().HandleAsync([new ActivationItem(ActivationUriKind.BillingSuccess, "wino://billing/success")]);

        _host.Verify(h => h.ShowSettingsAsync(WinoPage.WinoAccountManagementPage, WinoAccountManagementActivationReason.CheckoutCompleted), Times.Once);
        _accounts.Verify(a => a.GetAccountsAsync(), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Webcal_OpensTheCalendarOnlyWithAccounts(bool hasAccounts)
    {
        HasAccounts(hasAccounts);

        await CreateRouter().HandleAsync([new ActivationItem(ActivationUriKind.Webcal, "webcal://example.com/cal.ics")]);

        _host.Verify(h => h.EnsureShellAsync(WinoApplicationMode.Calendar, It.Is<object?>(p => p is CalendarPageNavigationArgs)),
            hasAccounts ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task CalendarFiles_AreImportedTogetherInsideTheirAccessScope()
    {
        HasAccounts(true);
        var first = new TestFile(WriteFile("one.ics"));
        var second = new TestFile(WriteFile("two.ics"));
        IReadOnlyList<string>? imported = null;
        _importer.Setup(i => i.ImportCalendarEventAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>, CancellationToken>((paths, _) =>
            {
                imported = paths;
                first.Opened.Should().Be(1);
                first.Closed.Should().Be(0);
            })
            .ReturnsAsync((CalendarEventComposeNavigationArgs?)null);

        await CreateRouter().HandleAsync(
        [
            new ActivationItem(ActivationUriKind.CalendarFile, "file:///one.ics", first),
            new ActivationItem(ActivationUriKind.CalendarFile, "file:///two.ics", second)
        ]);

        imported.Should().Equal(first.Path, second.Path);
        first.Closed.Should().Be(1);
        second.Closed.Should().Be(1);
        // A failed import still opens the calendar and says so.
        _host.Verify(h => h.EnsureShellAsync(WinoApplicationMode.Calendar, It.Is<object?>(p => p is CalendarPageNavigationArgs)), Times.Once);
        _dialogs.Verify(d => d.InfoBarMessage(It.IsAny<string>(), It.IsAny<string>(), InfoBarMessageType.Warning), Times.Once);
    }

    [Fact]
    public async Task UnsupportedFiles_BecomeAPendingShareRequest()
    {
        HasAccounts(true);
        var path = WriteFile("report.pdf", "pdf");
        var file = new TestFile(path);

        await CreateRouter().HandleAsync([new ActivationItem(ActivationUriKind.Unsupported, "file:///report.pdf", file)]);

        var request = _state.Object.PendingShareRequest;
        Assert.NotNull(request);
        request.Files.Should().ContainSingle().Which.FullFilePath.Should().Be(path);
        file.Opened.Should().Be(1);
        file.Closed.Should().Be(1);
        _host.Verify(h => h.EnsureShellAsync(WinoApplicationMode.Mail, null), Times.Once);
    }

    [Fact]
    public async Task MailFiles_OpenWithoutAnAccountCheckOncePerPath()
    {
        var path = WriteFile("message.eml", "Subject: Hi\r\n\r\nBody");
        _host.Setup(h => h.OpenMailFileAsync(path, It.IsAny<byte[]>())).ReturnsAsync(true);

        await CreateRouter().HandleAsync(
        [
            new ActivationItem(ActivationUriKind.MailFile, "file:///message.eml", new TestFile(path)),
            new ActivationItem(ActivationUriKind.MailFile, "file:///message.eml", new TestFile(path.ToUpperInvariant()))
        ]);

        _host.Verify(h => h.OpenMailFileAsync(It.IsAny<string>(), It.Is<byte[]>(b => b.Length > 0)), Times.Once);
        _accounts.Verify(a => a.GetAccountsAsync(), Times.Never);
        _dialogs.Verify(d => d.ShowMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<WinoCustomMessageDialogIcon>()), Times.Never);
    }

    [Fact]
    public async Task UnreadableMailFile_ShowsAMessage()
    {
        await CreateRouter().HandleAsync([new ActivationItem(ActivationUriKind.MailFile, "file:///missing.eml", new TestFile(Path.Combine(_folder, "missing.eml")))]);

        _host.Verify(h => h.OpenMailFileAsync(It.IsAny<string>(), It.IsAny<byte[]>()), Times.Never);
        _dialogs.Verify(d => d.ShowMessageAsync(It.IsAny<string>(), It.IsAny<string>(), WinoCustomMessageDialogIcon.Warning), Times.Once);
    }
}
