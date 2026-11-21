using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.UI;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.ViewModels.Links;
using Wino.Mail.ViewModels.Requests;
using Wino.Services.Interfaces;

namespace Wino.Mail.ViewModels.FlaggedMail;

/// <summary>
/// The Flagged mail view of To Do: flagged mails of every account. Completing a row clears the mail's flag through
/// the mail request pipeline, so the row leaves at once and comes back if the server refuses.
/// </summary>
public sealed partial class FlaggedMailListViewModel : ObservableObject,
    IRecipient<MailStateUpdatedMessage>,
    IRecipient<BulkMailStateUpdatedMessage>,
    IRecipient<MailUpdatedMessage>,
    IRecipient<BulkMailUpdatedMessage>,
    IRecipient<MailAddedMessage>,
    IRecipient<BulkMailAddedMessage>,
    IRecipient<MailRemovedMessage>,
    IRecipient<BulkMailRemovedMessage>
{
    private const int MaxItems = 500;
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(400);

    private readonly IMessenger _messenger;
    private readonly IMailService _mailService;
    private readonly WinoRequestCoordinator _requestCoordinator;
    private readonly ConnectedItemsCoordinator _connectedItems;
    private readonly IPlatformCapabilities _platformCapabilities;
    private readonly IDispatcher _dispatcher;
    private readonly ILogger _logger = Log.ForContext<FlaggedMailListViewModel>();
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private int _reloadScheduled;
    private bool _isAttached;

    public FlaggedMailListViewModel(
        IMessenger messenger,
        IMailService mailService,
        WinoRequestCoordinator requestCoordinator,
        ConnectedItemsCoordinator connectedItems,
        IPlatformCapabilities platformCapabilities,
        IDispatcher dispatcher)
    {
        _messenger = messenger;
        _mailService = mailService;
        _requestCoordinator = requestCoordinator;
        _connectedItems = connectedItems;
        _platformCapabilities = platformCapabilities;
        _dispatcher = dispatcher;
    }

    /// <summary>The head renders this view. Other heads keep it out of the To Do pane until they do.</summary>
    public bool IsAvailable => _platformCapabilities?.FlaggedMailTaskView == true;

    public ObservableCollection<FlaggedMailItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; set; }

    public bool IsEmpty => !IsLoading && Items.Count == 0;

    /// <summary>Raised on the UI thread when the number of rows changed, for the pane's count.</summary>
    public event EventHandler CountChanged;

    /// <summary>Listens for flag changes while To Do is shown and loads the list.</summary>
    public void Attach()
    {
        if (!IsAvailable || _isAttached)
            return;

        _isAttached = true;
        _messenger.Register<MailStateUpdatedMessage>(this);
        _messenger.Register<BulkMailStateUpdatedMessage>(this);
        _messenger.Register<MailUpdatedMessage>(this);
        _messenger.Register<BulkMailUpdatedMessage>(this);
        _messenger.Register<MailAddedMessage>(this);
        _messenger.Register<BulkMailAddedMessage>(this);
        _messenger.Register<MailRemovedMessage>(this);
        _messenger.Register<BulkMailRemovedMessage>(this);
        _ = ReloadAsync();
    }

    public void Detach()
    {
        if (!_isAttached)
            return;

        _isAttached = false;
        _messenger.UnregisterAll(this);
    }

    public async Task ReloadAsync()
    {
        if (!IsAvailable)
            return;

        await _reloadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _dispatcher.ExecuteOnUIThread(() => IsLoading = Items.Count == 0).ConfigureAwait(false);

            List<MailCopy> mails;
            try
            {
                mails = await _mailService.GetFlaggedMailsAsync(MaxItems).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Could not load flagged mail.");
                mails = [];
            }

            await _dispatcher.ExecuteOnUIThread(() =>
            {
                Reconcile(mails);
                IsLoading = false;
                NotifyCountChanged();
            }).ConfigureAwait(false);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    [RelayCommand]
    private async Task CompleteAsync(FlaggedMailItemViewModel item)
    {
        if (item is null || item.IsCompleting)
            return;

        item.IsCompleting = true;
        var result = await _requestCoordinator.ExecuteMailOperationAsync(
            new MailOperationPreperationRequest(MailOperation.ClearFlag, item.Mail)).ConfigureAwait(false);

        if (result.Error is not null)
            await _dispatcher.ExecuteOnUIThread(() => item.IsCompleting = false).ConfigureAwait(false);
    }

    [RelayCommand]
    private Task OpenAsync(FlaggedMailItemViewModel item)
        => item is null || _connectedItems is null ? Task.CompletedTask : _connectedItems.OpenMailAsync(item.Mail);

    private void Reconcile(IReadOnlyList<MailCopy> mails)
    {
        var desired = mails.Select(static mail => mail.UniqueId).ToHashSet();
        for (var index = Items.Count - 1; index >= 0; index--)
        {
            if (!desired.Contains(Items[index].UniqueId))
                Items.RemoveAt(index);
        }

        for (var index = 0; index < mails.Count; index++)
        {
            var currentIndex = IndexOf(mails[index].UniqueId);
            if (currentIndex < 0)
                Items.Insert(index, new FlaggedMailItemViewModel(mails[index]));
            else if (currentIndex != index)
                Items.Move(currentIndex, index);
        }
    }

    private int IndexOf(Guid uniqueId)
    {
        for (var index = 0; index < Items.Count; index++)
        {
            if (Items[index].UniqueId == uniqueId)
                return index;
        }

        return -1;
    }

    private void Remove(IEnumerable<Guid> uniqueIds)
    {
        var ids = uniqueIds.ToHashSet();
        if (ids.Count == 0)
            return;

        _ = _dispatcher.ExecuteOnUIThread(() =>
        {
            var removed = false;
            for (var index = Items.Count - 1; index >= 0; index--)
            {
                if (ids.Contains(Items[index].UniqueId))
                {
                    Items.RemoveAt(index);
                    removed = true;
                }
            }

            if (removed)
                NotifyCountChanged();
        });
    }

    private void ScheduleReload()
    {
        if (Interlocked.Exchange(ref _reloadScheduled, 1) == 1)
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(ReloadDelay).ConfigureAwait(false);
            Interlocked.Exchange(ref _reloadScheduled, 0);
            if (_isAttached)
                await ReloadAsync().ConfigureAwait(false);
        });
    }

    private void NotifyCountChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        CountChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Apply(IEnumerable<MailStateChange> changes)
    {
        var flagged = changes.Where(static change => change.IsFlagged.HasValue).ToList();
        Remove(flagged.Where(static change => change.IsFlagged == false).Select(static change => change.UniqueId));
        if (flagged.Any(static change => change.IsFlagged == true))
            ScheduleReload();
    }

    private void Apply(IEnumerable<MailCopy> mails, MailCopyChangeFlags changedProperties)
    {
        if (changedProperties != MailCopyChangeFlags.None && (changedProperties & MailCopyChangeFlags.IsFlagged) == 0)
            return;

        var list = mails.Where(static mail => mail is not null).ToList();
        Remove(list.Where(static mail => !mail.IsFlagged).Select(static mail => mail.UniqueId));
        if (list.Any(static mail => mail.IsFlagged))
            ScheduleReload();
    }

    public void Receive(MailStateUpdatedMessage message) => Apply([message.UpdatedState]);
    public void Receive(BulkMailStateUpdatedMessage message) => Apply(message.UpdatedStates);
    public void Receive(MailUpdatedMessage message) => Apply([message.UpdatedMail], message.ChangedProperties);
    public void Receive(BulkMailUpdatedMessage message) => Apply(message.UpdatedMails, message.ChangedProperties);

    public void Receive(MailAddedMessage message)
    {
        if (message.AddedMail?.IsFlagged == true)
            ScheduleReload();
    }

    public void Receive(BulkMailAddedMessage message)
    {
        if (message.AddedMails?.Any(static mail => mail?.IsFlagged == true) == true)
            ScheduleReload();
    }

    public void Receive(MailRemovedMessage message)
    {
        if (message.RemovedMail is { } mail)
            Remove([mail.UniqueId]);
    }

    public void Receive(BulkMailRemovedMessage message)
        => Remove(message.RemovedMails.Where(static mail => mail is not null).Select(static mail => mail.UniqueId));
}
