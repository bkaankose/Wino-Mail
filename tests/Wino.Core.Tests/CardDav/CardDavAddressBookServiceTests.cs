using FluentAssertions;
using Moq;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;
using Xunit;

namespace Wino.Core.Tests.CardDav;

public sealed class CardDavAddressBookServiceTests
{
    [Fact]
    public async Task CreateAsync_ServerWithoutCollectionCreationCapability_IsRejectedBeforeRequest()
    {
        var accountId = Guid.NewGuid();
        var account = new MailAccount
        {
            Id = accountId,
            Address = "user@example.test",
            ServerInformation = new CustomServerInformation
            {
                AccountId = accountId,
                CardDavServiceUrl = "https://dav.example.test/",
                CalDavUsername = "user@example.test"
            }
        };
        var client = new Mock<ICardDavClient>(MockBehavior.Strict);
        var store = new Mock<ICardDavSynchronizationStore>();
        store.Setup(item => item.GetAccountStateAsync(accountId)).ReturnsAsync(new CardDavAccountState
        {
            AccountId = accountId,
            AddressBookHomeHref = "https://dav.example.test/address-books/user/",
            SupportsAddressBookCreation = false
        });
        var accounts = new Mock<IAccountService>();
        accounts.Setup(item => item.GetAccountAsync(accountId)).ReturnsAsync(account);
        var credentials = new Mock<IDavCredentialStore>();
        credentials.Setup(item => item.GetPasswordAsync(accountId, It.IsAny<CancellationToken>())).ReturnsAsync("app-password");
        var service = new CardDavAddressBookService(
            client.Object,
            store.Object,
            credentials.Object,
            accounts.Object,
            Mock.Of<IContactService>());

        var action = () => service.CreateAsync(accountId, "New book");

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(Translator.ContactsPage_AddressBookCreationUnsupported);
        client.VerifyNoOtherCalls();
    }
}
