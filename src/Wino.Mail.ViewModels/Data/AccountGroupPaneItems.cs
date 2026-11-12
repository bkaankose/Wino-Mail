using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Accounts;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// The account group rows of one mode pane. Rows are kept per group so a rebuild keeps their containers, an expansion
/// toggled by the user is saved, and a toggle made in another mode's pane is applied here without a rebuild.
/// </summary>
public sealed class AccountGroupPaneItems : IRecipient<AccountGroupExpansionChanged>
{
    private readonly Dictionary<Guid, AccountGroupMenuItem> _items = [];
    private readonly IAccountService _accountService;
    private readonly Func<IDispatcher?> _dispatcherProvider;

    public AccountGroupPaneItems(IAccountService accountService, Func<IDispatcher?> dispatcherProvider)
    {
        _accountService = accountService;
        _dispatcherProvider = dispatcherProvider;

        WeakReferenceMessenger.Default.Register(this);
    }

    public IEnumerable<AccountGroupMenuItem> Items => _items.Values;

    /// <summary>
    /// Turns the layout into the pane's top-level account entries. <paramref name="mapEntry"/> returns the pane row of
    /// an account or linked account, or null to leave it out; a group whose entries are all left out is not shown.
    /// </summary>
    public List<IMenuItem> Build(IEnumerable<AccountLayoutEntry> layout, Func<AccountLayoutEntry, IMenuItem?> mapEntry)
    {
        var result = new List<IMenuItem>();
        var activeGroupIds = new HashSet<Guid>();

        foreach (var entry in layout ?? Enumerable.Empty<AccountLayoutEntry>())
        {
            if (entry.Kind != AccountLayoutEntryKind.Group)
            {
                if (mapEntry(entry) is { } row)
                    result.Add(row);

                continue;
            }

            if (entry.Group is not { } group)
                continue;

            var children = entry.Children
                .Select(mapEntry)
                .Where(child => child != null)
                .Select(child => child!)
                .ToList();

            if (children.Count == 0)
                continue;

            var groupItem = GetOrCreate(group);
            ApplyChildren(groupItem.SubMenuItems, children);
            groupItem.NotifyChildrenChanged();

            activeGroupIds.Add(group.Id);
            result.Add(groupItem);
        }

        foreach (var groupId in _items.Keys.Where(id => !activeGroupIds.Contains(id)).ToList())
        {
            Release(groupId);
        }

        return result;
    }

    /// <summary>Puts every row of a group back at the top level and forgets the groups.</summary>
    public void Clear()
    {
        foreach (var groupId in _items.Keys.ToList())
        {
            Release(groupId);
        }
    }

    /// <summary>
    /// Updates <paramref name="items"/> to <paramref name="desired"/>. The unchanged leading rows stay and the rest is
    /// replaced, because a navigation view can fail on moves and inserts in the middle of a nested item source.
    /// </summary>
    public static void ApplyChildren(ObservableCollection<IMenuItem> items, IReadOnlyList<IMenuItem> desired)
    {
        var keepCount = 0;
        while (keepCount < items.Count && keepCount < desired.Count && ReferenceEquals(items[keepCount], desired[keepCount]))
        {
            keepCount++;
        }

        for (var index = items.Count - 1; index >= keepCount; index--)
        {
            items.RemoveAt(index);
        }

        for (var index = keepCount; index < desired.Count; index++)
        {
            items.Add(desired[index]);
        }
    }

    private AccountGroupMenuItem GetOrCreate(MailAccountGroup group)
    {
        if (_items.TryGetValue(group.Id, out var item))
        {
            item.Update(group);
            return item;
        }

        item = new AccountGroupMenuItem(group);
        item.PropertyChanged += GroupItemPropertyChanged;
        _items.Add(group.Id, item);

        return item;
    }

    private void Release(Guid groupId)
    {
        if (!_items.Remove(groupId, out var item))
            return;

        item.PropertyChanged -= GroupItemPropertyChanged;
        item.SubMenuItems.Clear();
    }

    private async void GroupItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AccountGroupMenuItem.IsExpanded) ||
            sender is not AccountGroupMenuItem { Parameter: { } group } item ||
            group.IsExpanded == item.IsExpanded)
        {
            return;
        }

        group.IsExpanded = item.IsExpanded;

        try
        {
            await _accountService.SetAccountGroupExpandedAsync(group.Id, item.IsExpanded).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not save the expansion of account group {GroupId}.", group.Id);
        }
    }

    public void Receive(AccountGroupExpansionChanged message)
    {
        void Apply()
        {
            if (!_items.TryGetValue(message.GroupId, out var item) || item.Parameter is not { } group)
                return;

            // The entity first, so the property change below is not saved again.
            group.IsExpanded = message.IsExpanded;
            item.IsExpanded = message.IsExpanded;
        }

        if (_dispatcherProvider() is { } dispatcher)
            _ = dispatcher.ExecuteOnUIThread(Apply);
        else
            Apply();
    }
}
