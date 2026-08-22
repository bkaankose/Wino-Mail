using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.Controls.Core;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels.Collections;

/// <summary>
/// Serialized, flat storage for the active mail list. UI projection, grouping,
/// threading, expansion, and selection are owned by WinoMailListView.
/// </summary>
public sealed class MailListStore
{
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public Func<MailCopy, MailItemViewModel> MailItemFactory { get; set; } =
        static mailCopy => new MailItemViewModel(mailCopy);

    public IDispatcher CoreDispatcher { get; set; }

    public MailListCollection<MailItemViewModel> Items { get; } = [];

    public int Count => Items.Count;

    public int AllItemsCount => Items.Count;

    public IReadOnlyCollection<Guid> ItemIds => Items.ItemIds;

    public Task ClearAsync() =>
        RunSerializedAsync(() => ExecuteUIThread(() =>
        {
            using (Items.DeferRefresh())
            {
                Items.Clear();
            }
        }));

    public bool ContainsThreadId(string threadId) =>
        !string.IsNullOrWhiteSpace(threadId) &&
        ((IEnumerable<MailItemViewModel>)Items).Any(
            item => string.Equals(item.ThreadKey, threadId, StringComparison.Ordinal));

    /// <summary>
    /// Returns true when another copy of the same server message is already listed under a
    /// different <see cref="MailCopy.UniqueId"/>. Gmail materializes one row per label, so a
    /// single incoming message arrives as several rows that must not all be shown.
    /// </summary>
    public bool ContainsOtherServerMailCopy(MailCopy mailCopy)
    {
        if (mailCopy == null)
            return false;

        var accountId = mailCopy.AssignedAccount?.Id ?? Guid.Empty;
        var serverMailId = mailCopy.Id;

        // Fail open for rows we cannot identify, so nothing is ever hidden by accident.
        if (accountId == Guid.Empty || string.IsNullOrWhiteSpace(serverMailId))
            return false;

        return ((IEnumerable<MailItemViewModel>)Items).Any(item =>
            item.UniqueId != mailCopy.UniqueId &&
            string.Equals(item.Id, serverMailId, StringComparison.Ordinal) &&
            item.MailCopy?.AssignedAccount?.Id == accountId);
    }

    public bool ContainsMailUniqueId(Guid uniqueId) => Items.ContainsId(uniqueId);

    public MailItemViewModel Find(Guid uniqueId) =>
        Items.TryGetItem(uniqueId, out MailItemViewModel item) ? item : null;

    public void UpdateAccountNicknamePosition(AccountNicknamePosition position)
    {
        foreach (var mailItem in Items)
        {
            mailItem.AccountNicknamePosition = position;
        }
    }

    public Task AddAsync(MailCopy addedItem) =>
        RunSerializedAsync(() => ExecuteUIThread(() =>
        {
            if (addedItem is null)
            {
                return;
            }

            using (Items.DeferRefresh())
            {
                if (Items.TryGetItem(addedItem.UniqueId, out MailItemViewModel existing))
                {
                    existing.UpdateFrom(addedItem);
                    return;
                }

                Items.TryAdd(MailItemFactory(addedItem));
            }
        }));

    public Task AddRangeAsync(
        IEnumerable<MailItemViewModel> items,
        bool clearIdCache,
        Func<bool> shouldApply = null) =>
        RunSerializedAsync(() => ExecuteUIThread(() =>
        {
            if (shouldApply?.Invoke() == false)
            {
                return;
            }

            var incoming = items?
                .Where(static item => item is not null)
                .GroupBy(static item => item.UniqueId)
                .Select(static group => group.Last())
                .ToArray() ?? [];

            using (Items.DeferRefresh())
            {
                if (clearIdCache)
                {
                    Items.ReplaceAll(incoming);
                    return;
                }

                var additions = new List<MailItemViewModel>(incoming.Length);
                foreach (var item in incoming)
                {
                    if (Items.TryGetItem(item.UniqueId, out MailItemViewModel existing))
                    {
                        existing.UpdateFrom(item.MailCopy);
                    }
                    else
                    {
                        additions.Add(item);
                    }
                }

                Items.AddRange(additions);
            }
        }));

    public Task UpdateThumbnailsForAddressAsync(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return Task.CompletedTask;
        }

        return RunSerializedAsync(() => ExecuteUIThread(() =>
        {
            foreach (var mailItem in Items)
            {
                if (mailItem.FromAddress?.Equals(address, StringComparison.OrdinalIgnoreCase) == true)
                {
                    mailItem.ThumbnailUpdatedEvent = !mailItem.ThumbnailUpdatedEvent;
                }
            }
        }));
    }

    public Task UpdateMailCopy(
        MailCopy updatedMailCopy,
        EntityUpdateSource mailUpdateSource,
        MailCopyChangeFlags changedProperties = MailCopyChangeFlags.None) =>
        RunSerializedAsync(() => ExecuteUIThread(() =>
        {
            if (updatedMailCopy is null ||
                !Items.TryGetItem(updatedMailCopy.UniqueId, out MailItemViewModel existing))
            {
                return;
            }

            using (Items.DeferRefresh())
            {
                existing.UpdateFrom(updatedMailCopy, changedProperties);
                existing.IsBusy = mailUpdateSource == EntityUpdateSource.ClientUpdated;
            }
        }));

    public Task UpdateMailCopiesAsync(
        IEnumerable<MailCopy> updatedMailCopies,
        EntityUpdateSource mailUpdateSource,
        MailCopyChangeFlags changedProperties = MailCopyChangeFlags.None) =>
        RunSerializedAsync(() => ExecuteUIThread(() =>
        {
            var copies = updatedMailCopies?
                .Where(static copy => copy is not null)
                .GroupBy(static copy => copy.UniqueId)
                .Select(static group => group.Last())
                .ToArray() ?? [];

            using (Items.DeferRefresh())
            {
                foreach (var copy in copies)
                {
                    if (!Items.TryGetItem(copy.UniqueId, out MailItemViewModel existing))
                    {
                        continue;
                    }

                    existing.UpdateFrom(copy, changedProperties);
                    existing.IsBusy = mailUpdateSource == EntityUpdateSource.ClientUpdated;
                }
            }
        }));

    public Task UpdateMailStateAsync(
        MailStateChange updatedState,
        EntityUpdateSource mailUpdateSource) =>
        RunSerializedAsync(() => ExecuteUIThread(() =>
        {
            if (updatedState is null ||
                !Items.TryGetItem(updatedState.UniqueId, out MailItemViewModel existing))
            {
                return;
            }

            existing.ApplyStateChanges(updatedState.IsRead, updatedState.IsFlagged);
            existing.IsBusy = mailUpdateSource == EntityUpdateSource.ClientUpdated;
        }));

    public Task UpdateMailStatesAsync(
        IEnumerable<MailStateChange> updatedStates,
        EntityUpdateSource mailUpdateSource) =>
        RunSerializedAsync(() => ExecuteUIThread(() =>
        {
            var states = updatedStates?
                .Where(static state => state is not null)
                .GroupBy(static state => state.UniqueId)
                .Select(static group => group.Last())
                .ToArray() ?? [];

            foreach (var state in states)
            {
                if (!Items.TryGetItem(state.UniqueId, out MailItemViewModel existing))
                {
                    continue;
                }

                existing.ApplyStateChanges(state.IsRead, state.IsFlagged);
                existing.IsBusy = mailUpdateSource == EntityUpdateSource.ClientUpdated;
            }
        }));

    public MailItemViewModel GetFirst() => Items.Count > 0 ? Items[0] : null;

    public MailItemViewModel GetNextItem(MailCopy mailCopy)
    {
        try
        {
            var index = ((IEnumerable<MailItemViewModel>)Items)
                .Select(static item => item.UniqueId)
                .ToList()
                .IndexOf(mailCopy.UniqueId);
            return index >= 0 && index + 1 < Items.Count ? Items[index + 1] : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to find the next item to select.");
            return null;
        }
    }

    public Task RemoveAsync(MailCopy removeItem) =>
        removeItem is null
            ? Task.CompletedTask
            : RemoveRangeByIdAsync(new[] { removeItem.UniqueId });

    public Task RemoveRangeAsync(IEnumerable<MailCopy> removeItems) =>
        RemoveRangeByIdAsync(removeItems?
            .Where(static item => item is not null)
            .Select(static item => item.UniqueId) ?? []);

    public Task RemoveRangeByIdAsync(IEnumerable<Guid> uniqueIds) =>
        RunSerializedAsync(() => ExecuteUIThread(() =>
        {
            using (Items.DeferRefresh())
            {
                Items.RemoveRangeById(uniqueIds);
            }
        }));

    public void Cleanup()
    {
        // The store has no messenger or item-level subscriptions.
    }

    private Task ExecuteUIThread(Action action)
    {
        if (CoreDispatcher is null)
        {
            action();
            return Task.CompletedTask;
        }

        return CoreDispatcher.ExecuteOnUIThread(action);
    }

    private async Task RunSerializedAsync(Func<Task> action)
    {
        await _mutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }
}
