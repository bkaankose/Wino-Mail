using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Messaging.UI;
using Wino.Core.Domain.Models.Links;
using Wino.Mail.ViewModels.Calendar.Data;
using Wino.Mail.ViewModels.Links;

namespace Wino.Mail.ViewModels.Calendar;

public partial class EventDetailsPageViewModel : IRecipient<ItemLinksChanged>
{
    private readonly ConnectedItemsCoordinator _connectedItems;
    private long _linkedItemsVersion;

    /// <summary>Mails and tasks linked to the event: the mail it came from, follow-up tasks, the task it blocks time for.</summary>
    public ObservableCollection<LinkedItemViewModel> LinkedItems { get; } = [];

    [ObservableProperty]
    public partial bool HasLinkedItems { get; set; }

    public bool CanCreateFollowUpTask => _connectedItems is not null;

    [RelayCommand]
    private Task CreateFollowUpTaskAsync()
        => CurrentEvent?.CalendarItem is { } item && _connectedItems is not null
            ? _connectedItems.CreateTaskFromEventAsync(item)
            : Task.CompletedTask;

    [RelayCommand]
    private Task OpenLinkedItemAsync(LinkedItemViewModel item)
        => _connectedItems?.OpenLinkedItemAsync(item) ?? Task.CompletedTask;

    private void BeginLinkedItemsLoad(CalendarItemViewModel calendarItem)
    {
        var version = Interlocked.Increment(ref _linkedItemsVersion);
        LinkedItems.Clear();
        HasLinkedItems = false;

        if (_connectedItems is null || calendarItem?.CalendarItem is not { } item)
            return;

        _ = LoadLinkedItemsAsync(ItemLinkKeys.EventCandidates(item), version);
    }

    private async Task LoadLinkedItemsAsync(System.Collections.Generic.List<string> keys, long version)
    {
        var items = await _connectedItems.GetLinkedItemsAsync(ItemLinkKind.Event, keys).ConfigureAwait(false);

        await ExecuteUIThread(() =>
        {
            if (version != Volatile.Read(ref _linkedItemsVersion))
                return;

            LinkedItems.Clear();
            foreach (var item in items)
                LinkedItems.Add(item);
            HasLinkedItems = LinkedItems.Count > 0;
        }).ConfigureAwait(false);
    }

    void IRecipient<ItemLinksChanged>.Receive(ItemLinksChanged message)
    {
        _ = ExecuteUIThread(() =>
        {
            if (CurrentEvent?.CalendarItem is not { } item)
                return;

            var keys = ItemLinkKeys.EventCandidates(item);
            if (message.Links.Any(link => (link.SourceKind == ItemLinkKind.Event && keys.Contains(link.SourceKey)) ||
                                          (link.TargetKind == ItemLinkKind.Event && keys.Contains(link.TargetKey))))
            {
                BeginLinkedItemsLoad(CurrentEvent);
            }
        });
    }
}
