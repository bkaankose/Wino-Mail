using FluentAssertions;
using MimeKit;
using Moq;
using System.IO;
using System.Text;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Extensions;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class ReadReceiptTrackingTests
{
    [Fact]
    public void SetReadReceiptRequest_WhenEnabled_AddsDispositionNotificationHeader()
    {
        var mime = new MimeMessage();

        mime.SetReadReceiptRequest("sender@example.com", true);

        mime.HasReadReceiptRequest().Should().BeTrue();
        mime.Headers[Constants.DispositionNotificationToHeader].Should().Be("sender@example.com");
    }

    [Fact]
    public void SetReadReceiptRequest_WhenDisabled_RemovesDispositionNotificationHeader()
    {
        var mime = new MimeMessage();
        mime.Headers.Add(Constants.DispositionNotificationToHeader, "sender@example.com");

        mime.SetReadReceiptRequest("sender@example.com", false);

        mime.HasReadReceiptRequest().Should().BeFalse();
    }

    [Fact]
    public void ParseReadReceipt_ExtractsOriginalMessageIdAndAcknowledgedTime()
    {
        var mime = new MimeMessage
        {
            Date = new DateTimeOffset(2026, 04, 10, 12, 30, 0, TimeSpan.Zero),
            Body = CreateDispositionNotificationBody("Final-Recipient: rfc822; recipient@example.com\r\nOriginal-Message-ID: <original@example.com>\r\nDisposition: manual-action/MDN-sent-manually; displayed\r\n")
        };

        var result = mime.ParseReadReceipt();

        result.IsReadReceipt.Should().BeTrue();
        result.OriginalMessageId.Should().Be("original@example.com");
        result.AcknowledgedAtUtc.Should().Be(new DateTime(2026, 04, 10, 12, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void AsOutlookMessage_WhenMimeRequestsReadReceipt_SetsGraphFlag()
    {
        var mime = new MimeMessage
        {
            Subject = "Test receipt request",
            Body = new TextPart("plain") { Text = "Hello" }
        };
        mime.MessageId = "test@example.com";
        mime.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        mime.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));
        mime.SetReadReceiptRequest("sender@example.com", true);

        var message = mime.AsOutlookMessage(includeInternetHeaders: false);

        message.IsReadReceiptRequested.Should().BeTrue();
    }

    [Fact]
    public async Task ProcessIncomingReceiptAsync_MatchesSentMailByMessageId_AndAcknowledgesState()
    {
        var db = new InMemoryDatabaseService();
        await db.InitializeAsync();

        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Address = "sender@example.com",
            SenderName = "Sender"
        };

        var sentFolder = new MailItemFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = account.Id,
            FolderName = "Sent",
            SpecialFolderType = SpecialFolderType.Sent
        };

        var sentMail = new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            Id = "sent-1",
            FolderId = sentFolder.Id,
            MessageId = "original@example.com",
            CreationDate = new DateTime(2026, 04, 10, 10, 0, 0, DateTimeKind.Utc)
        };

        await db.Connection.InsertAsync(account, typeof(MailAccount));
        await db.Connection.InsertAsync(sentFolder, typeof(MailItemFolder));
        await db.Connection.InsertAsync(sentMail, typeof(MailCopy));

        var folderService = new Mock<IFolderService>(MockBehavior.Strict);
        folderService.Setup(x => x.GetFolderAsync(sentFolder.Id)).ReturnsAsync(sentFolder);

        var accountService = new Mock<IAccountService>(MockBehavior.Strict);
        accountService.Setup(x => x.GetAccountAsync(account.Id)).ReturnsAsync(account);

        var service = new SentMailReceiptService(db, folderService.Object, accountService.Object);

        var receiptMail = new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            AssignedAccount = account
        };

        var receiptMime = new MimeMessage
        {
            Date = new DateTimeOffset(2026, 04, 10, 13, 15, 0, TimeSpan.Zero),
            Body = CreateDispositionNotificationBody("Original-Message-ID: <original@example.com>\r\nDisposition: manual-action/MDN-sent-manually; displayed\r\n")
        };

        await service.ProcessIncomingReceiptAsync(receiptMail, receiptMime);

        var receiptState = await db.Connection.FindAsync<SentMailReceiptState>(sentMail.UniqueId);
        receiptState.Should().NotBeNull();
        receiptState!.Status.Should().Be(SentMailReceiptStatus.Acknowledged);
        receiptState.ReceiptMessageUniqueId.Should().Be(receiptMail.UniqueId);
        receiptState.MessageId.Should().Be("original@example.com");
    }

    private static Multipart CreateDispositionNotificationBody(string dispositionText)
    {
        var report = new Multipart("report");
        report.ContentType.Parameters.Add("report-type", "disposition-notification");
        report.Add(new TextPart("plain") { Text = "Read receipt" });
        report.Add(new MimePart("message", "disposition-notification")
        {
            Content = new MimeContent(new MemoryStream(Encoding.UTF8.GetBytes(dispositionText)))
        });
        return report;
    }
}
