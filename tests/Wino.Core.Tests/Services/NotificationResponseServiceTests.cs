using FluentAssertions;
using MimeKit;
using Moq;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Notifications;
using Wino.Services.Notifications;
using Xunit;

namespace Wino.Core.Tests.Services;

public class NotificationResponseServiceTests
{
    private readonly Mock<ICalendarService> _calendar = new();
    private readonly Mock<IMailService> _mail = new();
    private readonly Mock<IFolderService> _folders = new();
    private readonly Mock<IAccountService> _accounts = new();
    private readonly Mock<IMimeFileService> _mime = new();
    private readonly Mock<IWinoRequestDelegator> _delegator = new();
    private readonly Mock<INotificationBuilder> _builder = new();
    private readonly Mock<IPreferencesService> _preferences = new();
    private readonly Mock<IExternalLauncher> _launcher = new();
    private readonly Mock<INotificationResponseNavigator> _navigator = new();

    private NotificationResponseService CreateService() => new(
        _calendar.Object, _mail.Object, _folders.Object, _accounts.Object, _mime.Object, _delegator.Object,
        _builder.Object, _preferences.Object, _launcher.Object, _navigator.Object);

    private static NotificationResponse Response(NotificationResponseKind kind, string action, string? userText, params (string Key, string Value)[] values)
        => new(kind, action, null, userText, values.ToDictionary(pair => pair.Key, pair => pair.Value));

    #region Snooze fallback chain

    [Theory]
    [InlineData("15", 30, 15)]   // the length the notification offered wins
    [InlineData(null, 30, 30)]   // older notification: the "Default snooze duration" setting
    [InlineData("0", 30, 30)]    // a non-positive offer is ignored
    [InlineData("abc", 30, 30)]  // an unreadable offer is ignored
    [InlineData(null, 0, NotificationActionIdentifiers.FallbackSnoozeMinutes)] // no setting: 5 minutes
    [InlineData("-3", -1, NotificationActionIdentifiers.FallbackSnoozeMinutes)]
    public void ResolveSnoozeMinutes_FollowsTheFallbackChain(string? offered, int preferred, int expected)
    {
        var values = new Dictionary<string, string>();
        if (offered is not null) values[Constants.ToastCalendarSnoozeDurationInputId] = offered;

        NotificationResponseService.ResolveSnoozeMinutes(values, preferred).Should().Be(expected);
    }

    [Fact]
    public async Task SnoozeAction_SnoozesForThePreferredLengthWhenTheNotificationOfferedNone()
    {
        var itemId = Guid.NewGuid();
        _preferences.SetupGet(p => p.DefaultSnoozeDurationInMinutes).Returns(20);
        DateTime? snoozedUntil = null;
        _calendar.Setup(c => c.SnoozeCalendarItemAsync(itemId, It.IsAny<DateTime>()))
            .Callback<Guid, DateTime>((_, until) => snoozedUntil = until)
            .Returns(Task.CompletedTask);

        var before = DateTime.Now;
        await CreateService().HandleAsync(Response(NotificationResponseKind.Action, NotificationActionIdentifiers.CalendarSnooze, null,
            (Constants.ToastCalendarItemIdKey, itemId.ToString())));

        Assert.NotNull(snoozedUntil);
        snoozedUntil.Value.Should().BeOnOrAfter(before.AddMinutes(20)).And.BeOnOrBefore(DateTime.Now.AddMinutes(20));
        _navigator.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SnoozeAction_UsesTheOfferedLength()
    {
        var itemId = Guid.NewGuid();
        _preferences.SetupGet(p => p.DefaultSnoozeDurationInMinutes).Returns(20);
        DateTime? snoozedUntil = null;
        _calendar.Setup(c => c.SnoozeCalendarItemAsync(itemId, It.IsAny<DateTime>()))
            .Callback<Guid, DateTime>((_, until) => snoozedUntil = until)
            .Returns(Task.CompletedTask);

        var before = DateTime.Now;
        await CreateService().HandleAsync(Response(NotificationResponseKind.Action, NotificationActionIdentifiers.CalendarSnooze, null,
            (Constants.ToastCalendarItemIdKey, itemId.ToString()),
            (Constants.ToastCalendarSnoozeDurationInputId, "10")));

        Assert.NotNull(snoozedUntil);
        snoozedUntil.Value.Should().BeOnOrAfter(before.AddMinutes(10)).And.BeOnOrBefore(DateTime.Now.AddMinutes(10));
    }

    #endregion

    #region Mode routing

    [Theory]
    [InlineData(Constants.ToastModeCalendar, WinoApplicationMode.Calendar)]
    [InlineData(Constants.ToastModePeople, WinoApplicationMode.Contacts)]
    [InlineData(Constants.ToastModeTasks, WinoApplicationMode.Tasks)]
    [InlineData(Constants.ToastModeMail, WinoApplicationMode.Mail)]
    [InlineData("unknown", WinoApplicationMode.Mail)]
    public async Task GenericTap_OpensTheNotificationMode(string modeValue, WinoApplicationMode expected)
    {
        await CreateService().HandleAsync(Response(NotificationResponseKind.Default, string.Empty, null, (Constants.ToastModeKey, modeValue)));

        _navigator.Verify(n => n.OpenModeAsync(expected, It.IsAny<object?>()), Times.Once);
        _navigator.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GenericOpenAction_WithoutMode_OpensMail()
    {
        await CreateService().HandleAsync(Response(NotificationResponseKind.Action, NotificationActionIdentifiers.Open, null));

        _navigator.Verify(n => n.OpenModeAsync(WinoApplicationMode.Mail, null), Times.Once);
    }

    [Fact]
    public async Task CalendarMode_RequestsDefaultCalendarNavigation()
    {
        object? parameter = null;
        _navigator.Setup(n => n.OpenModeAsync(WinoApplicationMode.Calendar, It.IsAny<object?>()))
            .Callback<WinoApplicationMode, object?>((_, p) => parameter = p)
            .Returns(Task.CompletedTask);

        await CreateService().HandleAsync(Response(NotificationResponseKind.Default, string.Empty, null, (Constants.ToastModeKey, Constants.ToastModeCalendar)));

        parameter.Should().BeOfType<CalendarPageNavigationArgs>().Which.RequestDefaultNavigation.Should().BeTrue();
    }

    [Fact]
    public async Task Dismiss_DoesNothing()
    {
        await CreateService().HandleAsync(Response(NotificationResponseKind.Dismiss, string.Empty, null,
            (Constants.ToastMailUniqueIdKey, Guid.NewGuid().ToString())));
        await CreateService().HandleAsync(Response(NotificationResponseKind.Action, NotificationActionIdentifiers.Dismiss, null,
            (Constants.ToastModeKey, Constants.ToastModeCalendar)));

        _navigator.VerifyNoOtherCalls();
        _mail.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MailTap_ForAMissingMail_OpensMailMode()
    {
        var mailId = Guid.NewGuid();
        // Without a setup the mock answers a missing account (null).
        await CreateService().HandleAsync(Response(NotificationResponseKind.Default, string.Empty, null, (Constants.ToastMailUniqueIdKey, mailId.ToString())));

        _navigator.Verify(n => n.OpenModeAsync(WinoApplicationMode.Mail, null), Times.Once);
        _navigator.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MailTap_OpensTheMailInItsFolder()
    {
        var account = new MailAccount { Id = Guid.NewGuid() };
        var folder = new MailItemFolder { Id = Guid.NewGuid() };
        var mailItem = new MailCopy { UniqueId = Guid.NewGuid(), AssignedFolder = folder };
        _mail.Setup(m => m.GetMailAccountByUniqueIdAsync(mailItem.UniqueId)).ReturnsAsync(account);
        _mail.Setup(m => m.GetSingleMailItemAsync(mailItem.UniqueId)).ReturnsAsync(mailItem);

        await CreateService().HandleAsync(Response(NotificationResponseKind.Default, string.Empty, null, (Constants.ToastMailUniqueIdKey, mailItem.UniqueId.ToString())));

        _navigator.Verify(n => n.OpenMailAsync(account.Id, folder.Id, mailItem), Times.Once);
    }

    [Fact]
    public async Task MailAction_ExecutesTheOperationAndRefreshesTheBadge()
    {
        var mailItem = new MailCopy { UniqueId = Guid.NewGuid() };
        _mail.Setup(m => m.GetSingleMailItemAsync(mailItem.UniqueId)).ReturnsAsync(mailItem);
        MailOperationPreperationRequest? request = null;
        _delegator.Setup(d => d.ExecuteAsync(It.IsAny<MailOperationPreperationRequest>()))
            .Callback<MailOperationPreperationRequest>(r => request = r)
            .Returns(Task.CompletedTask);

        await CreateService().HandleAsync(Response(NotificationResponseKind.Action, NotificationActionIdentifiers.MailAction(MailOperation.Archive), null,
            (Constants.ToastMailUniqueIdKey, mailItem.UniqueId.ToString())));

        Assert.NotNull(request);
        request.Action.Should().Be(MailOperation.Archive);
        request.MailItems.Should().ContainSingle().Which.Should().BeSameAs(mailItem);
        _builder.Verify(b => b.UpdateTaskbarIconBadgeAsync(), Times.Once);
    }

    [Fact]
    public async Task UnsupportedMailAction_IsIgnored()
    {
        await CreateService().HandleAsync(Response(NotificationResponseKind.Action, NotificationActionIdentifiers.MailAction(MailOperation.Move), null,
            (Constants.ToastMailUniqueIdKey, Guid.NewGuid().ToString())));

        _mail.VerifyNoOtherCalls();
        _delegator.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CalendarJoin_LaunchesTheMeetingLink()
    {
        var item = new CalendarItem { Id = Guid.NewGuid(), DirectJoinLink = "https://meet.example.com/abc" };
        _calendar.Setup(c => c.GetCalendarItemAsync(item.Id)).ReturnsAsync(item);

        await CreateService().HandleAsync(Response(NotificationResponseKind.Action, NotificationActionIdentifiers.CalendarJoin, null,
            (Constants.ToastCalendarItemIdKey, item.Id.ToString())));

        _launcher.Verify(l => l.LaunchUriAsync(new Uri("https://meet.example.com/abc"), It.IsAny<CancellationToken>()), Times.Once);
        _navigator.VerifyNoOtherCalls();
    }

    #endregion

    #region Inline reply

    private (MailAccount Account, MailCopy Original, MailCopy Draft, MailItemFolder DraftFolder) SetupReply(string draftFromAddress, MailItemFolder? sentFolder)
    {
        var account = new MailAccount { Id = Guid.NewGuid(), Preferences = new MailAccountPreferences() };
        var draftFolder = new MailItemFolder { Id = Guid.NewGuid(), SpecialFolderType = SpecialFolderType.Draft };
        var original = new MailCopy { UniqueId = Guid.NewGuid(), FileId = Guid.NewGuid(), AssignedAccount = account };
        var draft = new MailCopy { UniqueId = Guid.NewGuid(), AssignedFolder = draftFolder };

        var originalMime = new MimeMessage();
        originalMime.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        originalMime.Subject = "Hello";

        var draftMime = new MimeMessage();
        draftMime.From.Add(new MailboxAddress("Me", draftFromAddress));
        draftMime.Subject = "Re: Hello";

        _mail.Setup(m => m.GetSingleMailItemAsync(original.UniqueId)).ReturnsAsync(original);
        _mail.Setup(m => m.GetMailAccountByUniqueIdAsync(original.UniqueId)).ReturnsAsync(account);
        _folders.Setup(f => f.GetSpecialFolderByAccountIdAsync(account.Id, SpecialFolderType.Draft)).ReturnsAsync(draftFolder);
        // Without a setup the mock answers a missing Sent folder (null).
        if (sentFolder is not null)
            _folders.Setup(f => f.GetSpecialFolderByAccountIdAsync(account.Id, SpecialFolderType.Sent)).ReturnsAsync(sentFolder);
        _mime.Setup(m => m.GetMimeMessageInformationAsync(original.FileId, account.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MimeMessageInformation(originalMime, "original.eml"));
        _mail.Setup(m => m.CreateDraftAsync(account.Id, It.IsAny<DraftCreationOptions>()))
            .ReturnsAsync((draft, draftMime.GetBase64MimeMessage()));
        _navigator.Setup(n => n.ShowDraftsAsync(account.Id)).ReturnsAsync(true);

        return (account, original, draft, draftFolder);
    }

    [Fact]
    public async Task InlineReply_SendsFromTheAliasInTheDraftFromHeader()
    {
        var sentFolder = new MailItemFolder { Id = Guid.NewGuid(), SpecialFolderType = SpecialFolderType.Sent };
        var (account, original, draft, draftFolder) = SetupReply("Sales@Example.com", sentFolder);
        var primary = new MailAccountAlias { AliasAddress = "me@example.com", IsPrimary = true };
        var sales = new MailAccountAlias { AliasAddress = "sales@example.com" };
        _accounts.Setup(a => a.GetAccountAliasesAsync(account.Id)).ReturnsAsync([primary, sales]);
        _accounts.Setup(a => a.GetPrimaryAccountAliasAsync(account.Id)).ReturnsAsync(primary);
        SendDraftPreparationRequest? sent = null;
        _delegator.Setup(d => d.ExecuteAsync(It.IsAny<SendDraftPreparationRequest>()))
            .Callback<SendDraftPreparationRequest>(r => sent = r)
            .Returns(Task.CompletedTask);
        DraftCreationOptions? options = null;
        _mail.Setup(m => m.CreateDraftAsync(account.Id, It.IsAny<DraftCreationOptions>()))
            .Callback<Guid, DraftCreationOptions>((_, o) => options = o)
            .ReturnsAsync((draft, DraftMime("Sales@Example.com")));

        await CreateService().HandleAsync(Response(NotificationResponseKind.Action, NotificationActionIdentifiers.MailAction(MailOperation.Reply), "  Thanks!  ",
            (Constants.ToastMailUniqueIdKey, original.UniqueId.ToString())));

        Assert.NotNull(options);
        options.Reason.Should().Be(DraftCreationReason.Reply);
        options.InitialBodyText.Should().Be("Thanks!");
        Assert.NotNull(sent);
        sent.SendingAlias.Should().BeSameAs(sales);
        sent.SentFolder.Should().BeSameAs(sentFolder);
        sent.DraftFolder.Should().BeSameAs(draftFolder);
        sent.MailItem.Should().BeSameAs(draft);
        _accounts.Verify(a => a.GetPrimaryAccountAliasAsync(It.IsAny<Guid>()), Times.Never);
        _navigator.Verify(n => n.OpenComposerAsync(It.IsAny<MailCopy>()), Times.Never);
    }

    [Fact]
    public async Task InlineReply_FallsBackToThePrimaryAlias()
    {
        var sentFolder = new MailItemFolder { Id = Guid.NewGuid(), SpecialFolderType = SpecialFolderType.Sent };
        var (account, original, _, _) = SetupReply("unknown@example.com", sentFolder);
        var primary = new MailAccountAlias { AliasAddress = "me@example.com", IsPrimary = true };
        _accounts.Setup(a => a.GetAccountAliasesAsync(account.Id)).ReturnsAsync([primary]);
        _accounts.Setup(a => a.GetPrimaryAccountAliasAsync(account.Id)).ReturnsAsync(primary);
        SendDraftPreparationRequest? sent = null;
        _delegator.Setup(d => d.ExecuteAsync(It.IsAny<SendDraftPreparationRequest>()))
            .Callback<SendDraftPreparationRequest>(r => sent = r)
            .Returns(Task.CompletedTask);

        await CreateService().HandleAsync(Response(NotificationResponseKind.Action, NotificationActionIdentifiers.MailAction(MailOperation.Reply), "Ok",
            (Constants.ToastMailUniqueIdKey, original.UniqueId.ToString())));

        Assert.NotNull(sent);
        sent.SendingAlias.Should().BeSameAs(primary);
    }

    [Fact]
    public async Task InlineReply_WithoutASentFolder_OpensTheDraftInTheComposer()
    {
        var (account, original, draft, _) = SetupReply("me@example.com", sentFolder: null);
        var primary = new MailAccountAlias { AliasAddress = "me@example.com", IsPrimary = true };
        _accounts.Setup(a => a.GetAccountAliasesAsync(account.Id)).ReturnsAsync([primary]);

        await CreateService().HandleAsync(Response(NotificationResponseKind.Action, NotificationActionIdentifiers.MailAction(MailOperation.Reply), "Ok",
            (Constants.ToastMailUniqueIdKey, original.UniqueId.ToString())));

        _delegator.Verify(d => d.ExecuteAsync(It.IsAny<SendDraftPreparationRequest>()), Times.Never);
        _navigator.Verify(n => n.ShowDraftsAsync(account.Id), Times.Once);
        _navigator.Verify(n => n.OpenComposerAsync(draft), Times.Once);
        // The draft created for the send attempt is reused, not created twice.
        _mail.Verify(m => m.CreateDraftAsync(account.Id, It.IsAny<DraftCreationOptions>()), Times.Once);
    }

    [Fact]
    public async Task ReplyWithoutText_OpensTheComposer()
    {
        var (account, original, draft, _) = SetupReply("me@example.com", sentFolder: null);

        await CreateService().HandleAsync(Response(NotificationResponseKind.Action, NotificationActionIdentifiers.MailAction(MailOperation.Reply), "   ",
            (Constants.ToastMailUniqueIdKey, original.UniqueId.ToString())));

        _navigator.Verify(n => n.ShowDraftsAsync(account.Id), Times.Once);
        _navigator.Verify(n => n.OpenComposerAsync(draft), Times.Once);
        _delegator.Verify(d => d.ExecuteAsync(It.IsAny<DraftPreparationRequest>()), Times.Once);
        _delegator.Verify(d => d.ExecuteAsync(It.IsAny<SendDraftPreparationRequest>()), Times.Never);
    }

    [Theory]
    [InlineData("Sales@Example.com", "sales@example.com")]
    [InlineData("other@example.com", "me@example.com")]
    public async Task ResolveSendingAlias_MatchesTheFromHeaderCaseInsensitively(string from, string expectedAlias)
    {
        var accountId = Guid.NewGuid();
        var primary = new MailAccountAlias { AliasAddress = "me@example.com", IsPrimary = true };
        var sales = new MailAccountAlias { AliasAddress = "sales@example.com" };
        _accounts.Setup(a => a.GetAccountAliasesAsync(accountId)).ReturnsAsync([primary, sales]);
        _accounts.Setup(a => a.GetPrimaryAccountAliasAsync(accountId)).ReturnsAsync(primary);

        var alias = await CreateService().ResolveSendingAliasAsync(accountId, DraftMime(from));

        Assert.NotNull(alias);
        alias.AliasAddress.Should().Be(expectedAlias);
    }

    private static string DraftMime(string from)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Me", from));
        message.Subject = "Re: Hello";
        return message.GetBase64MimeMessage();
    }

    #endregion
}
