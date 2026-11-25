#nullable enable
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
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Links;

namespace Wino.Mail.ViewModels;

public partial class MailRenderingPageViewModel : IRecipient<ItemLinksChanged>
{
    private readonly ConnectedItemsCoordinator? _connectedItems;
    private long _linkedItemsVersion;
    private string? _linkedMailKey;

    /// <summary>Tasks and events created from the open message.</summary>
    public ObservableCollection<LinkedItemViewModel> LinkedItems { get; } = [];

    [ObservableProperty]
    public partial bool HasLinkedItems { get; set; }

    [RelayCommand]
    private Task OpenLinkedItemAsync(LinkedItemViewModel? item)
        => _connectedItems?.OpenLinkedItemAsync(item) ?? Task.CompletedTask;

    private void ResetLinkedItems()
    {
        Interlocked.Increment(ref _linkedItemsVersion);
        _linkedMailKey = null;
        _ = ExecuteUIThread(() =>
        {
            LinkedItems.Clear();
            HasLinkedItems = false;
        });
    }

    private void BeginLinkedItemsLoad(MailItemViewModel mailItem)
    {
        if (_connectedItems is null || mailItem.MailCopy is null)
            return;

        _linkedMailKey = ItemLinkKeys.ForMail(mailItem.MailCopy);
        _ = LoadLinkedItemsAsync(_linkedMailKey, Interlocked.Increment(ref _linkedItemsVersion));
    }

    private async Task LoadLinkedItemsAsync(string mailKey, long version)
    {
        if (_connectedItems is null)
            return;

        var items = await _connectedItems.GetLinkedItemsAsync(ItemLinkKind.Mail, [mailKey]).ConfigureAwait(false);

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
        var mailKey = _linkedMailKey;
        if (mailKey is null)
            return;

        if (message.Links.Any(link => (link.SourceKind == ItemLinkKind.Mail && link.SourceKey == mailKey) ||
                                      (link.TargetKind == ItemLinkKind.Mail && link.TargetKey == mailKey)))
        {
            _ = LoadLinkedItemsAsync(mailKey, Interlocked.Increment(ref _linkedItemsVersion));
        }
    }
}
