using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Core.AccountIcon;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class MailAccountIconInfoFactoryTests
{
    public static TheoryData<MailProviderType, SpecialImapProvider, AccountIconProvider> ProviderMappings => new()
    {
        { MailProviderType.Outlook, SpecialImapProvider.None, AccountIconProvider.Microsoft },
        { MailProviderType.Gmail, SpecialImapProvider.None, AccountIconProvider.Google },
        { MailProviderType.IMAP4, SpecialImapProvider.None, AccountIconProvider.Imap },
        { MailProviderType.IMAP4, SpecialImapProvider.iCloud, AccountIconProvider.ICloud },
        { MailProviderType.IMAP4, SpecialImapProvider.Yahoo, AccountIconProvider.Yahoo },
    };

    [Theory]
    [MemberData(nameof(ProviderMappings))]
    public void Create_MapsMailAccountProvider(
        MailProviderType providerType,
        SpecialImapProvider specialImapProvider,
        AccountIconProvider expectedProvider)
    {
        var service = new Mock<IAccountProfilePictureFileService>();
        var account = new MailAccount
        {
            ProviderType = providerType,
            SpecialImapProvider = specialImapProvider,
        };

        var result = MailAccountIconInfoFactory.Create(account, service.Object);

        result.Provider.Should().Be(expectedProvider);
    }

    [Fact]
    public void Create_ResolvesProfilePicturePathAndPreservesColor()
    {
        var fileId = Guid.NewGuid();
        var service = new Mock<IAccountProfilePictureFileService>();
        service.Setup(item => item.GetProfilePicturePath(fileId)).Returns(@"C:\pictures\account.jpg");
        var account = new MailAccount
        {
            ProviderType = MailProviderType.Gmail,
            ProfilePictureFileId = fileId,
            AccountColorHex = "#336699",
        };

        var result = MailAccountIconInfoFactory.Create(account, service.Object);

        result.ProfilePicturePath.Should().Be(@"C:\pictures\account.jpg");
        result.AccountColorHex.Should().Be("#336699");
    }

    [Fact]
    public void Create_MissingProfilePictureReturnsNullPath()
    {
        var fileId = Guid.NewGuid();
        var service = new Mock<IAccountProfilePictureFileService>();
        service.Setup(item => item.GetProfilePicturePath(fileId)).Returns((string?)null);
        var account = new MailAccount
        {
            ProviderType = MailProviderType.Outlook,
            ProfilePictureFileId = fileId,
        };

        var result = MailAccountIconInfoFactory.Create(account, service.Object);

        result.ProfilePicturePath.Should().BeNull();
    }

    [Fact]
    public void Create_AccountWithoutProfilePictureDoesNotQueryFileService()
    {
        var service = new Mock<IAccountProfilePictureFileService>(MockBehavior.Strict);
        var account = new MailAccount { ProviderType = MailProviderType.Outlook };

        var result = MailAccountIconInfoFactory.Create(account, service.Object);

        result.ProfilePicturePath.Should().BeNull();
        service.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(ProviderMappings))]
    public void CreateProviderFallback_MapsProviderWithoutAccount(
        MailProviderType providerType,
        SpecialImapProvider specialImapProvider,
        AccountIconProvider expectedProvider)
    {
        var result = MailAccountIconInfoFactory.CreateProviderFallback(providerType, specialImapProvider);

        result.Provider.Should().Be(expectedProvider);
        result.ProfilePicturePath.Should().BeNull();
        result.AccountColorHex.Should().BeNull();
    }
}
