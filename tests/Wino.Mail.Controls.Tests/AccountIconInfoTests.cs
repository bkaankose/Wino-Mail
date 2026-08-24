using FluentAssertions;
using Wino.Mail.Controls.Core.AccountIcon;
using Xunit;

namespace Wino.Mail.Controls.Tests;

public sealed class AccountIconInfoTests
{
    [Fact]
    public void AccountIconInfo_PreservesProjectionValues()
    {
        IAccountIconInfo info = new AccountIconInfo(
            AccountIconProvider.ICloud,
            @"C:\pictures\profile.jpg",
            "#336699");

        info.Provider.Should().Be(AccountIconProvider.ICloud);
        info.ProfilePicturePath.Should().Be(@"C:\pictures\profile.jpg");
        info.AccountColorHex.Should().Be("#336699");
    }

    [Fact]
    public void AccountIconProvider_ContainsEverySupportedProvider()
    {
        Enum.GetValues<AccountIconProvider>().Should().BeEquivalentTo(
            (AccountIconProvider[])
            [
                AccountIconProvider.Microsoft,
                AccountIconProvider.Google,
                AccountIconProvider.ICloud,
                AccountIconProvider.Yahoo,
                AccountIconProvider.Imap,
            ]);
    }
}
