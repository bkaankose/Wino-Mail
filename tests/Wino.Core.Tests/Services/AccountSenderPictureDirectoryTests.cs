using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.UI;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class AccountSenderPictureDirectoryTests
{
    private readonly Mock<IAccountService> _accountService = new();
    private readonly Mock<IPictureStorageService> _pictureStorage = new();
    private readonly StrongReferenceMessenger _messenger = new();

    [Fact]
    public async Task InitializeAsync_MapsAccountAddressToStoredPicture_CaseInsensitive()
    {
        var account = CreateAccount("second@gmail.com", @"C:\pictures\second.jpg");
        _accountService.Setup(s => s.GetAccountsAsync()).ReturnsAsync([account, CreateAccount("nopicture@outlook.com", null)]);
        var directory = CreateDirectory();

        await directory.InitializeAsync();

        directory.GetProfilePicturePath(" Second@Gmail.com ").Should().Be(@"C:\pictures\second.jpg");
        directory.GetProfilePicturePath("nopicture@outlook.com").Should().BeNull();
        directory.GetProfilePicturePath("stranger@example.com").Should().BeNull();
        directory.GetProfilePicturePath(null).Should().BeNull();
    }

    [Fact]
    public void AccountMessages_KeepMapCurrent()
    {
        var directory = CreateDirectory();
        var account = CreateAccount("second@gmail.com", @"C:\pictures\first.jpg");

        _messenger.Send(new AccountCreatedMessage(account));
        directory.GetProfilePicturePath("second@gmail.com").Should().Be(@"C:\pictures\first.jpg");

        // A synchronizer replaced the picture.
        SetPicture(account, @"C:\pictures\replaced.jpg");
        _messenger.Send(new AccountUpdatedMessage(account));
        directory.GetProfilePicturePath("second@gmail.com").Should().Be(@"C:\pictures\replaced.jpg");

        // The address changed: the old address must no longer resolve.
        account.Address = "renamed@gmail.com";
        _messenger.Send(new AccountUpdatedMessage(account));
        directory.GetProfilePicturePath("second@gmail.com").Should().BeNull();
        directory.GetProfilePicturePath("renamed@gmail.com").Should().Be(@"C:\pictures\replaced.jpg");

        // The picture was removed.
        account.ProfilePictureFileId = null;
        _messenger.Send(new AccountUpdatedMessage(account));
        directory.GetProfilePicturePath("renamed@gmail.com").Should().BeNull();

        SetPicture(account, @"C:\pictures\again.jpg");
        _messenger.Send(new AccountUpdatedMessage(account));
        _messenger.Send(new AccountRemovedMessage(account));
        directory.GetProfilePicturePath("renamed@gmail.com").Should().BeNull();
    }

    private AccountSenderPictureDirectory CreateDirectory()
        => new(_accountService.Object, _pictureStorage.Object, _messenger);

    private MailAccount CreateAccount(string address, string picturePath)
    {
        var account = new MailAccount { Id = Guid.NewGuid(), Address = address };

        if (picturePath is not null)
            SetPicture(account, picturePath);

        return account;
    }

    private void SetPicture(MailAccount account, string picturePath)
    {
        var fileId = Guid.NewGuid();
        account.ProfilePictureFileId = fileId;
        _pictureStorage.Setup(s => s.GetPicturePath(PictureKind.AccountProfile, fileId)).Returns(picturePath);
    }
}
