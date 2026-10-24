using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Core.HoverActions;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// Immutable snapshot of the hover and swipe actions every account resolves to. Hosts replace the
/// whole instance when a preference changes, which is what makes realized rows pick up the change.
/// </summary>
public sealed class MailListActionResolver : IMailListActionResolver
{
    private readonly HoverMailActions _defaultHoverActions;
    private readonly SwipeMailActions _defaultSwipeActions;
    private readonly Dictionary<Guid, HoverMailActions> _hoverActionsByAccount;
    private readonly Dictionary<Guid, SwipeMailActions> _swipeActionsByAccount;

    public MailListActionResolver(HoverMailActions defaultHoverActions, SwipeMailActions defaultSwipeActions)
        : this(defaultHoverActions, defaultSwipeActions, [], [])
    {
    }

    private MailListActionResolver(
        HoverMailActions defaultHoverActions,
        SwipeMailActions defaultSwipeActions,
        Dictionary<Guid, HoverMailActions> hoverActionsByAccount,
        Dictionary<Guid, SwipeMailActions> swipeActionsByAccount)
    {
        _defaultHoverActions = defaultHoverActions;
        _defaultSwipeActions = defaultSwipeActions;
        _hoverActionsByAccount = hoverActionsByAccount;
        _swipeActionsByAccount = swipeActionsByAccount;
    }

    public static MailListActionResolver Create(IPreferencesService preferences, IEnumerable<MailAccount> accounts)
    {
        var defaultHover = MailListActionPreferences.GetDefaultHoverActions(preferences);
        var defaultSwipe = MailListActionPreferences.GetDefaultSwipeActions(preferences);
        var hoverByAccount = new Dictionary<Guid, HoverMailActions>();
        var swipeByAccount = new Dictionary<Guid, SwipeMailActions>();

        foreach (var account in accounts)
        {
            if (account.Preferences is not { } accountPreferences) continue;

            if (MailListActionPreferences.HasCustomHoverActions(accountPreferences, defaultHover))
            {
                hoverByAccount[account.Id] = MailListActionPreferences.GetCustomHoverActions(accountPreferences);
            }

            if (MailListActionPreferences.HasCustomSwipeActions(accountPreferences, defaultSwipe))
            {
                swipeByAccount[account.Id] = MailListActionPreferences.GetCustomSwipeActions(accountPreferences);
            }
        }

        return new MailListActionResolver(defaultHover, defaultSwipe, hoverByAccount, swipeByAccount);
    }

    public HoverActionSet ResolveHoverActions(MailListRow row)
    {
        var actions = GetAccountId(row) is { } accountId && _hoverActionsByAccount.TryGetValue(accountId, out var custom)
            ? custom
            : _defaultHoverActions;

        return new HoverActionSet(ToHoverActionKind(actions.Left), ToHoverActionKind(actions.Center), ToHoverActionKind(actions.Right));
    }

    public SwipeMailActions ResolveSwipeActions(MailListRow row)
        => GetAccountId(row) is { } accountId && _swipeActionsByAccount.TryGetValue(accountId, out var custom)
            ? custom
            : _defaultSwipeActions;

    public static HoverActionKind ToHoverActionKind(MailOperation operation) => operation switch
    {
        MailOperation.Archive => HoverActionKind.Archive,
        MailOperation.SoftDelete => HoverActionKind.Delete,
        MailOperation.SetFlag or MailOperation.ClearFlag => HoverActionKind.ToggleFlag,
        MailOperation.MarkAsRead or MailOperation.MarkAsUnread => HoverActionKind.ToggleRead,
        MailOperation.MoveToJunk => HoverActionKind.MoveToJunk,
        _ => HoverActionKind.None,
    };

    // Threads never mix accounts, so the first leaf decides for the whole row.
    private static Guid? GetAccountId(MailListRow row)
        => row.LeafItems.OfType<MailItemViewModel>().FirstOrDefault()?.MailCopy?.AssignedAccount?.Id;
}
