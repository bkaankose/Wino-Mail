using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// The account group rows of one mode pane. Rows are kept per group so a rebuild keeps their containers. Every group
/// row the pane shows, including a combined Mail group, takes its expansion from <see cref="AccountGroupExpansionStore"/>
/// and reports a change of it there, so all modes show the same state.
/// </summary>
public sealed class AccountGroupPaneItems
{
    private readonly Dictionary<Guid, AccountGroupMenuItem> _items = [];
    private readonly Dictionary<Guid, MenuItemBase> _expansionRows = [];
    private readonly AccountGroupExpansionStore _expansionStore;
    private readonly Func<IDispatcher?> _dispatcherProvider;

    public AccountGroupPaneItems(AccountGroupExpansionStore expansionStore, Func<IDispatcher?> dispatcherProvider)
    {
        _expansionStore = expansionStore;
        _dispatcherProvider = dispatcherProvider;

        _expansionStore.ExpansionChanged += StoreExpansionChanged;
    }

    public IEnumerable<AccountGroupMenuItem> Items => _items.Values;

    /// <summary>
    /// Turns the layout into the pane's top-level account entries. <paramref name="mapEntry"/> returns the pane row of
    /// an account, or null to leave it out. It is asked for a group too: a row returned for it, such as a combined
    /// Mail group, replaces the group row; otherwise a group row holds the group's accounts and is not shown when
    /// all of them are left out.
    /// </summary>
    public List<IMenuItem> Build(IEnumerable<AccountLayoutEntry> layout, Func<AccountLayoutEntry, IMenuItem?> mapEntry)
    {
        var result = new List<IMenuItem>();
        var activeGroupIds = new HashSet<Guid>();
        var shownGroupIds = new HashSet<Guid>();

        foreach (var entry in layout ?? Enumerable.Empty<AccountLayoutEntry>())
        {
            if (mapEntry(entry) is { } row)
            {
                if (entry.Kind == AccountLayoutEntryKind.Group && entry.Group is { } combinedGroup && row is MenuItemBase combinedRow)
                {
                    TrackExpansion(combinedGroup, combinedRow);
                    shownGroupIds.Add(combinedGroup.Id);
                }

                result.Add(row);
                continue;
            }

            if (entry.Kind != AccountLayoutEntryKind.Group)
                continue;

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
            TrackExpansion(group, groupItem);
            ApplyChildren(groupItem.SubMenuItems, children);
            groupItem.NotifyChildrenChanged();

            activeGroupIds.Add(group.Id);
            shownGroupIds.Add(group.Id);
            result.Add(groupItem);
        }

        foreach (var groupId in _items.Keys.Where(id => !activeGroupIds.Contains(id)).ToList())
        {
            Release(groupId);
        }

        foreach (var groupId in _expansionRows.Keys.Where(id => !shownGroupIds.Contains(id)).ToList())
        {
            UntrackExpansion(groupId);
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

        foreach (var groupId in _expansionRows.Keys.ToList())
        {
            UntrackExpansion(groupId);
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

    private AccountGroupMenuItem GetOrCreate(MergedInbox group)
    {
        if (_items.TryGetValue(group.Id, out var item))
        {
            item.Update(group);
            return item;
        }

        item = new AccountGroupMenuItem(group);
        _items.Add(group.Id, item);

        return item;
    }

    private void Release(Guid groupId)
    {
        if (_items.Remove(groupId, out var item))
            item.SubMenuItems.Clear();
    }

    private void TrackExpansion(MergedInbox group, MenuItemBase row)
    {
        if (_expansionRows.TryGetValue(group.Id, out var trackedRow))
        {
            if (!ReferenceEquals(trackedRow, row))
            {
                trackedRow.PropertyChanged -= RowPropertyChanged;
                _expansionRows.Remove(group.Id);
            }
        }

        row.IsExpanded = _expansionStore.IsExpanded(group);

        if (_expansionRows.TryAdd(group.Id, row))
            row.PropertyChanged += RowPropertyChanged;
    }

    private void UntrackExpansion(Guid groupId)
    {
        if (_expansionRows.Remove(groupId, out var row))
            row.PropertyChanged -= RowPropertyChanged;
    }

    private void RowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MenuItemBase.IsExpanded) && sender is MenuItemBase { EntityId: Guid groupId } row)
            _expansionStore.SetExpanded(groupId, row.IsExpanded);
    }

    private void StoreExpansionChanged(Guid groupId, bool isExpanded)
    {
        void Apply()
        {
            if (_expansionRows.TryGetValue(groupId, out var row))
                row.IsExpanded = isExpanded;
        }

        if (_dispatcherProvider() is { } dispatcher)
            _ = dispatcher.ExecuteOnUIThread(Apply);
        else
            Apply();
    }
}
