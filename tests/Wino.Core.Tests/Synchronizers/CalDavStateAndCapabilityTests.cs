using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Services;
using Wino.Services.CalDav;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public sealed class CalDavStateAndCapabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wino-caldav-state-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task StateStore_RoundTripsEveryField_AtTheDocumentedPath()
    {
        var store = new CalDavStateStore(() => _root);
        var accountId = Guid.NewGuid();
        var state = new CalDavAccountState
        {
            PrincipalHref = "https://dav.example.test/123/principal/",
            CalendarHomeHrefs = ["https://dav.example.test/123/calendars/"],
            CalendarUserAddresses = ["mailto:user@example.test"],
            DavCapabilities = ["calendar-auto-schedule"],
            DiscoveredAtUtc = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc)
        };
        state.Calendars["https://dav.example.test/123/calendars/work"] = new CalDavCalendarState
        {
            RemoteCalendarId = "https://dav.example.test/123/calendars/work",
            SupportedComponents = ["VEVENT"],
            SupportsSyncCollection = true,
            SyncCollectionRefused = true,
            QuotaAvailableBytes = 42,
            MaxAttendeesPerInstance = 300,
            LastSyncToken = "token-7",
            LastSyncTokenFromReport = true,
            LastCTag = "ctag-7"
        };

        await store.SaveAsync(accountId, state);
        var loaded = await store.GetAsync(accountId);

        File.Exists(Path.Combine(_root, accountId.ToString("N"), "caldav-state.json")).Should().BeTrue();
        loaded.Should().BeEquivalentTo(state);
        loaded.FindCalendar("HTTPS://dav.example.test/123/calendars/WORK").Should().NotBeNull("remote calendar IDs are compared without case");
        File.ReadAllText(store.GetStatePath(accountId)).Should().NotContainEquivalentOf("password");
    }

    [Fact]
    public async Task StateStore_CorruptOrMissingFile_ReadsAsEmptyState()
    {
        var store = new CalDavStateStore(() => _root);
        var accountId = Guid.NewGuid();

        (await store.GetAsync(accountId)).HasDiscovery.Should().BeFalse();

        Directory.CreateDirectory(Path.GetDirectoryName(store.GetStatePath(accountId))!);
        await File.WriteAllTextAsync(store.GetStatePath(accountId), "{ not json");

        var state = await store.GetAsync(accountId);
        state.HasDiscovery.Should().BeFalse();
        state.Calendars.Should().BeEmpty();

        await store.UpdateAsync(accountId, value => value.PrincipalHref = "https://dav.example.test/p/");
        (await store.GetAsync(accountId)).PrincipalHref.Should().Be("https://dav.example.test/p/", "the next write replaces the unreadable file");
    }

    [Fact]
    public async Task CapabilityService_UsesStaticSetsForProviderAndLocalCalendars()
    {
        var outlook = Account(MailProviderType.Outlook, AccountIntegrationSource.Provider);
        var gmail = Account(MailProviderType.Gmail, AccountIntegrationSource.Provider);
        var local = Account(MailProviderType.IMAP4, AccountIntegrationSource.Local);
        var service = CreateService([outlook, gmail, local], [], new CalDavAccountState());

        (await service.GetAccountCapabilitiesAsync(outlook.Id)).Should().Be(CalendarCapabilities.Outlook);
        (await service.GetAccountCapabilitiesAsync(gmail.Id)).Should().Be(CalendarCapabilities.Gmail);

        var localCapabilities = await service.GetAccountCapabilitiesAsync(local.Id);
        localCapabilities.SupportsAttachments.Should().BeTrue();
        localCapabilities.SupportsAttendees.Should().BeTrue();
        localCapabilities.SendsInvitations.Should().BeFalse();

        (await service.GetAccountCapabilitiesAsync(Guid.NewGuid())).Should().Be(CalendarCapabilities.None);
    }

    [Fact]
    public async Task CapabilityService_CalDavWithoutDiscovery_ReturnsConservativeDefaults()
    {
        var account = CalDavAccount();
        var service = CreateService([account], [], new CalDavAccountState());

        var capabilities = await service.GetAccountCapabilitiesAsync(account.Id);

        capabilities.Should().Be(CalendarCapabilities.CalDavDefault);
        capabilities.SupportsAttachments.Should().BeFalse();
        capabilities.SendsInvitations.Should().BeFalse();
        capabilities.IsServerReported.Should().BeFalse();
    }

    [Fact]
    public async Task CapabilityService_CalDavCalendar_UsesItsOwnStateAndLocalReadOnlyFlag()
    {
        var account = CalDavAccount();
        var calendar = new AccountCalendar
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            RemoteCalendarId = "https://dav.example.test/cal/shared",
            IsReadOnly = true
        };
        var state = new CalDavAccountState
        {
            CalendarHomeHrefs = ["https://dav.example.test/cal/"],
            DiscoveredAtUtc = DateTime.UtcNow,
            DavCapabilities = ["calendar-access"]
        };
        state.Calendars[calendar.RemoteCalendarId] = new CalDavCalendarState
        {
            RemoteCalendarId = calendar.RemoteCalendarId,
            MaxAttendeesPerInstance = 50,
            MaxResourceSize = 1000,
            QuotaAvailableBytes = 10
        };
        var service = CreateService([account], [calendar], state);

        var capabilities = await service.GetCalendarCapabilitiesAsync(calendar.Id);

        capabilities.Source.Should().Be(CalendarCapabilitySource.CalDav);
        capabilities.IsReadOnly.Should().BeTrue();
        capabilities.MaxAttendees.Should().Be(50);
        capabilities.MaxResourceSize.Should().Be(1000);
        capabilities.QuotaAvailableBytes.Should().Be(10);
        capabilities.SupportsAttachments.Should().BeFalse("the server does not advertise managed attachments");
        capabilities.SendsInvitations.Should().BeFalse();
    }

    private static CalendarCapabilityService CreateService(IEnumerable<MailAccount> accounts, IEnumerable<AccountCalendar> calendars, CalDavAccountState state)
    {
        var accountService = new Mock<IAccountService>();
        foreach (var account in accounts)
            accountService.Setup(service => service.GetAccountAsync(account.Id)).ReturnsAsync(account);

        var calendarService = new Mock<ICalendarService>();
        foreach (var calendar in calendars)
            calendarService.Setup(service => service.GetAccountCalendarAsync(calendar.Id)).ReturnsAsync(calendar);

        var stateStore = new Mock<ICalDavStateStore>();
        stateStore.Setup(store => store.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(state);

        return new CalendarCapabilityService(accountService.Object, calendarService.Object, stateStore.Object);
    }

    private static MailAccount Account(MailProviderType providerType, AccountIntegrationSource source)
        => new()
        {
            Id = Guid.NewGuid(),
            ProviderType = providerType,
            CalendarIntegrationSource = source,
            ServerInformation = new CustomServerInformation { CalendarSupportMode = ImapCalendarSupportMode.LocalOnly }
        };

    private static MailAccount CalDavAccount()
        => new()
        {
            Id = Guid.NewGuid(),
            ProviderType = MailProviderType.IMAP4,
            CalendarIntegrationSource = AccountIntegrationSource.Dav,
            ServerInformation = new CustomServerInformation { CalendarSupportMode = ImapCalendarSupportMode.CalDav }
        };
}
