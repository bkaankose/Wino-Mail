using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;
using Wino.Core.Synchronizers;
using Wino.Core.Synchronizers.CalDav;
using Wino.Core.Synchronizers.CardDav;

namespace Wino.Core.Providers;

/// <summary>
/// CalDAV and CardDAV. They are not a mail provider, so they register no module: IMAP accounts use them for calendar
/// and To Do, and IMAP, Outlook and Gmail accounts for contacts, through the engine contracts in Wino.Core.Domain.
/// </summary>
public static class DavProviderRegistration
{
    public static IServiceCollection AddDavProvider(this IServiceCollection services)
    {
        services.AddSingleton<DavEndpointCache>();
        services.AddTransient<ICardDavSynchronizationEngine, CardDavSynchronizationEngine>();
        services.AddTransient<ICalDavSynchronizationEngine, CalDavSynchronizationEngine>();
        services.AddTransient<ICalDavInvitationEngine, CalDavSynchronizationEngine>();
        services.AddTransient<ICalDavTaskSynchronizationEngine, CalDavTaskSynchronizationEngine>();
        services.AddTransient<ICardDavAddressBookService, CardDavAddressBookService>();
        services.AddTransient<ICardDavContactListService, CardDavContactListService>();

        return services;
    }
}
