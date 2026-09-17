using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PublicFolders;
using Wino.Messaging.UI;

namespace Wino.Calendar.ViewModels;

/// <summary>
/// Pinned Exchange public calendar folders in the calendar pane. Each pin is a transient, read-only calendar
/// under its account, ticked like any other. Nothing about it is stored except the tick, which lives with
/// the pin; <see cref="CalendarPageViewModel"/> reads its events live for the visible range.
/// </summary>
public partial class CalendarAppShellViewModel : IRecipient<PublicFolderFavoritesChanged>
{
    private readonly IPublicFolderFavoriteService _publicFolderFavoriteService;

    /// <summary>Adds the pinned public calendars of the Exchange accounts that show a calendar.</summary>
    internal async Task AddPublicFolderCalendarsAsync(IReadOnlyList<MailAccount> accounts)
    {
        foreach (var (account, favorite) in GetPinnedPublicCalendars(accounts))
        {
            var viewModel = new AccountCalendarViewModel(account, PublicFolderCalendarFactory.Create(favorite));

            await Dispatcher.ExecuteOnUIThread(() => AccountCalendarStateService.AddAccountCalendar(viewModel));
        }
    }

    private IEnumerable<(MailAccount Account, PublicFolderFavorite Favorite)> GetPinnedPublicCalendars(IReadOnlyList<MailAccount> accounts)
    {
        if (_publicFolderFavoriteService == null || accounts == null)
            yield break;

        var exchangeAccounts = accounts
            .Where(account => account.ProviderType == MailProviderType.Exchange && GroupedAccountCalendarViewModel.SupportsCalendar(account))
            .ToDictionary(account => account.Id);

        foreach (var favorite in _publicFolderFavoriteService.GetFavorites())
        {
            if (favorite.Kind == PublicFolderKind.Calendar &&
                !string.IsNullOrEmpty(favorite.FolderId) &&
                exchangeAccounts.TryGetValue(favorite.AccountId, out var account))
            {
                yield return (account, favorite);
            }
        }
    }

    /// <summary>
    /// A public calendar has no stored row, so its tick goes to the pin instead of the calendar table.
    /// Returns true when the calendar was a public one and has been dealt with.
    /// </summary>
    internal bool TryPersistPublicFolderCalendarState(AccountCalendarViewModel calendar)
    {
        if (calendar?.IsPublicFolder != true)
            return false;

        _publicFolderFavoriteService?.SetFavoriteChecked(calendar.AccountId, calendar.RemoteCalendarId, calendar.IsChecked);
        return true;
    }

    public async void Receive(PublicFolderFavoritesChanged message)
    {
        // While another mode is on screen the pane holds no calendars; the next activation reads the pins.
        if (message.Kind != PublicFolderKind.Calendar || !_runtimeSubscriptionsAttached)
            return;

        try
        {
            await ReconcilePublicFolderCalendarsAsync(message.AccountId).ConfigureAwait(false);

            if (_lazyCalendarPageViewModel.IsValueCreated)
                await CalendarPage.ReloadCurrentVisibleRangeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to refresh the pinned public calendars of account {AccountId}.", message.AccountId);
        }
    }

    /// <summary>Drops the public calendars of an account that are no longer pinned and adds the new pins.</summary>
    internal async Task ReconcilePublicFolderCalendarsAsync(Guid accountId)
    {
        var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
        var pinned = GetPinnedPublicCalendars(accounts)
            .Where(pin => pin.Account.Id == accountId)
            .ToList();
        var pinnedIds = pinned
            .Select(pin => PublicFolderCalendarFactory.GetCalendarId(pin.Account.Id, pin.Favorite.FolderId))
            .ToHashSet();

        await ExecuteUIThread(() =>
        {
            var shown = AccountCalendarStateService.AllCalendars
                .Where(calendar => calendar.IsPublicFolder && calendar.AccountId == accountId)
                .ToList();

            foreach (var calendar in shown.Where(calendar => !pinnedIds.Contains(calendar.Id)))
            {
                AccountCalendarStateService.RemoveAccountCalendar(calendar);
            }

            foreach (var (account, favorite) in pinned.Where(pin => shown.All(calendar => calendar.RemoteCalendarId != pin.Favorite.FolderId)))
            {
                AccountCalendarStateService.AddAccountCalendar(new AccountCalendarViewModel(account, PublicFolderCalendarFactory.Create(favorite)));
            }
        }).ConfigureAwait(false);
    }
}
