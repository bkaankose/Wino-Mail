using System;
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
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Links;

namespace Wino.Mail.ViewModels;

public partial class ToDoPageViewModel : IRecipient<ItemLinksChanged>
{
    private readonly ConnectedItemsCoordinator _connectedItems;
    private long _selectedTaskLinksVersion;

    /// <summary>The mails and events the selected task was created from or scheduled with.</summary>
    public ObservableCollection<LinkedItemViewModel> SelectedTaskLinks { get; } = [];

    [ObservableProperty]
    public partial bool HasSelectedTaskLinks { get; set; }

    [RelayCommand]
    private Task OpenLinkedItemAsync(LinkedItemViewModel item)
        => _connectedItems?.OpenLinkedItemAsync(item) ?? Task.CompletedTask;

    private async Task ReloadAndOpenRequestedTaskAsync(object parameters)
    {
        await ReloadAsync().ConfigureAwait(false);
        await OpenRequestedTaskAsync(parameters).ConfigureAwait(false);
    }

    private async Task OpenRequestedTaskAsync(object parameters)
    {
        if (parameters is not ToDoNavigationArgs { } args)
            return;

        if (args.TaskId is Guid taskId)
            await LoadAndSelectTaskAsync(taskId).ConfigureAwait(false);
        else if (args.CreateList)
            await ExecuteUIThreadAsync(CreateListAsync).ConfigureAwait(false);
    }

    private void BeginSelectedTaskLinksLoad(TaskItemViewModel item)
    {
        var version = Interlocked.Increment(ref _selectedTaskLinksVersion);
        SelectedTaskLinks.Clear();
        HasSelectedTaskLinks = false;

        if (item is null || _connectedItems is null)
            return;

        _ = LoadSelectedTaskLinksAsync(item, version);
    }

    private async Task LoadSelectedTaskLinksAsync(TaskItemViewModel item, long version)
    {
        var links = await _connectedItems.GetLinkedItemsAsync(ItemLinkKind.Task, [ItemLinkKeys.ForTask(item.Id)]).ConfigureAwait(false);

        await ExecuteUIThread(() =>
        {
            if (version != Volatile.Read(ref _selectedTaskLinksVersion))
                return;

            SelectedTaskLinks.Clear();
            foreach (var link in links)
                SelectedTaskLinks.Add(link);
            HasSelectedTaskLinks = SelectedTaskLinks.Count > 0;
        }).ConfigureAwait(false);
    }

    void IRecipient<ItemLinksChanged>.Receive(ItemLinksChanged message)
    {
        _ = ExecuteUIThread(() =>
        {
            if (SelectedTask is not { } selected)
                return;

            var key = ItemLinkKeys.ForTask(selected.Id);
            if (message.Links.Any(link => (link.SourceKind == ItemLinkKind.Task && link.SourceKey == key) ||
                                          (link.TargetKind == ItemLinkKind.Task && link.TargetKey == key)))
            {
                BeginSelectedTaskLinksLoad(selected);
            }
        });
    }
}
