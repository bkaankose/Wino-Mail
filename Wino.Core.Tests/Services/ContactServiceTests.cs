using FluentAssertions;
using MimeKit;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class ContactServiceTests : IAsyncLifetime
{
    private InMemoryDatabaseService _databaseService = null!;
    private ContactService _contactService = null!;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();
        _contactService = new ContactService(_databaseService);
    }

    public async Task DisposeAsync()
    {
        await _databaseService.DisposeAsync();
    }

    [Fact]
    public async Task SaveAddressInformationAsync_WithNotificationReplyAddress_DoesNotPersistContact()
    {
        await _contactService.SaveAddressInformationAsync(
        [
            new AccountContact
            {
                Address = "reply+ABCD1234@reply.github.com",
                Name = "[owner/repository] Issue #42"
            }
        ]);

        var contact = await _databaseService.Connection
            .Table<AccountContact>()
            .Where(c => c.Address == "reply+ABCD1234@reply.github.com")
            .FirstOrDefaultAsync();

        contact.Should().BeNull();
    }

    [Fact]
    public async Task SaveAddressInformationAsync_WithHumanContact_PersistsContact()
    {
        await _contactService.SaveAddressInformationAsync(
        [
            new AccountContact
            {
                Address = "alice@example.com",
                Name = "Alice Example"
            }
        ]);

        var contact = await _databaseService.Connection
            .Table<AccountContact>()
            .Where(c => c.Address == "alice@example.com")
            .FirstOrDefaultAsync();

        contact.Should().NotBeNull();
        contact!.Name.Should().Be("Alice Example");
    }

    [Fact]
    public async Task SaveAddressInformationAsync_WithExistingNoisyContact_RemovesAutoCapturedEntry()
    {
        await _databaseService.Connection.InsertAsync(
            new AccountContact
            {
                Address = "notifications@github.com",
                Name = "GitHub Notifications"
            },
            typeof(AccountContact));

        await _contactService.SaveAddressInformationAsync(
        [
            new AccountContact
            {
                Address = "notifications@github.com",
                Name = "[owner/repository] Issue #99"
            }
        ]);

        var contact = await _databaseService.Connection
            .Table<AccountContact>()
            .Where(c => c.Address == "notifications@github.com")
            .FirstOrDefaultAsync();

        contact.Should().BeNull();
    }

    [Fact]
    public async Task SaveAddressInformationAsync_WithNoisyMimeGroup_SkipsGroupAndNoisyMembers()
    {
        var message = new MimeMessage();
        message.To.Add(new GroupAddress("[owner/repository] Issue #123", new InternetAddressList
        {
            new MailboxAddress("Alice Example", "alice@example.com"),
            new MailboxAddress("[owner/repository] Issue #123", "notifications@github.com")
        }));

        await _contactService.SaveAddressInformationAsync(message);

        var contacts = await _databaseService.Connection.Table<AccountContact>().ToListAsync();
        var groups = await _databaseService.Connection.Table<ContactGroup>().ToListAsync();

        contacts.Select(c => c.Address).Should().Contain("alice@example.com");
        contacts.Select(c => c.Address).Should().NotContain("notifications@github.com");
        groups.Should().BeEmpty();
    }
}
