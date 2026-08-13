using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class SpecialImapProviderConfigResolverTests
{
    [Fact]
    public void GetServerInformation_ICloud_UsesMailboxLocalPartForIncomingAndOutgoingUsernames()
    {
        var sut = new SpecialImapProviderConfigResolver();
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Address = "tester@icloud.com"
        };
        var dialogResult = new AccountCreationDialogResult(
            MailProviderType.IMAP4,
            "iCloud",
            new SpecialImapProviderDetails(
                "tester@icloud.com",
                "app-password",
                "Tester",
                SpecialImapProvider.iCloud,
                ImapCalendarSupportMode.CalDav),
            "#0078D4",
            InitialSynchronizationRange.SixMonths,
            true,
            true);

        var serverInformation = sut.GetServerInformation(account, dialogResult);

        serverInformation.IncomingServerUsername.Should().Be("tester");
        serverInformation.OutgoingServerUsername.Should().Be("tester");
        serverInformation.CalDavUsername.Should().Be("tester@icloud.com");
    }
}
