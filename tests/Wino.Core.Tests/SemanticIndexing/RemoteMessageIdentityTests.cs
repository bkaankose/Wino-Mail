using FluentAssertions;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.AI.Abstractions;
using Xunit;

namespace Wino.Core.Tests.SemanticIndexing;

public sealed class RemoteMessageIdentityTests
{
    [Theory]
    [InlineData(MailProviderType.Outlook, "message")]
    [InlineData(MailProviderType.Gmail, "message")]
    public void TryCreate_UsesProviderMessageIdentity(MailProviderType provider, string id)
    {
        var mail = CreateMail(provider, id);
        var expected = provider == MailProviderType.Outlook
            ? RemoteMessageId.ForOutlook(id)
            : RemoteMessageId.ForGmail(id);
        RemoteMessageIdentity.TryCreate(mail).Should().Be(expected);
    }

    [Fact]
    public void TryCreate_UsesImapFolderUidValidityAndUid()
    {
        var mail = CreateMail(MailProviderType.IMAP4, "ignored");
        mail.AssignedFolder!.RemoteFolderId = "INBOX";
        mail.ImapUidValidity = 42;
        mail.ImapUid = 99;

        RemoteMessageIdentity.TryCreate(mail).Should().Be(RemoteMessageId.ForImap("INBOX", 42, 99));
    }

    private static MailCopy CreateMail(MailProviderType provider, string id)
        => new()
        {
            Id = id,
            AssignedAccount = new MailAccount { Id = Guid.NewGuid(), ProviderType = provider },
            AssignedFolder = new MailItemFolder { RemoteFolderId = "folder", UidValidity = 1 },
        };
}
