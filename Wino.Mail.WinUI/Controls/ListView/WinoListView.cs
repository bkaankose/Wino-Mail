using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.WinUI.Controls.ListView;

public partial class WinoListView : Microsoft.UI.Xaml.Controls.ListView
{
    private const string PART_ScrollViewer = "ScrollViewer";
    private ScrollViewer? internalScrollviewer;

    [GeneratedDependencyProperty]
    public partial bool IsThreadListView { get; set; }

    [GeneratedDependencyProperty]
    public partial ICommand? LoadMoreCommand { get; set; }

    public event EventHandler<MailDragStateChangedEventArgs>? MailDragStateChanged;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        DragItemsStarting -= ItemDragStarting;
        DragItemsStarting += ItemDragStarting;
        DragItemsCompleted -= ItemDragCompleted;
        DragItemsCompleted += ItemDragCompleted;

        internalScrollviewer = GetTemplateChild(PART_ScrollViewer) as ScrollViewer;

        if (internalScrollviewer == null) return;

        internalScrollviewer.ViewChanged -= InternalScrollVeiwerViewChanged;
        internalScrollviewer.ViewChanged += InternalScrollVeiwerViewChanged;
    }

    private void InternalScrollVeiwerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (internalScrollviewer == null) return;

        // No need to raise init request if there are no items in the list.
        if (ItemsSource == null) return;

        double progress = internalScrollviewer.VerticalOffset / internalScrollviewer.ScrollableHeight;

        // Trigger when scrolled past 90% of total height
        if (progress >= 0.9)
        {
            bool canLoadMore = LoadMoreCommand?.CanExecute(null) ?? false;

            if (canLoadMore)
            {
                LoadMoreCommand?.Execute(null);
            }
        }
    }

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);

        // Ensure the container's selection state matches the model's state
        // This is crucial for virtualization scenarios where containers are recycled

        if (item is MailItemViewModel mailItemViewModel
            && element is WinoMailItemViewModelListViewItem container
            && container.Item != mailItemViewModel)
        {
            container.Item = mailItemViewModel;
        }
        else if (item is ThreadMailItemViewModel threadMailItemViewModel
            && element is WinoThreadMailItemViewModelListViewItem threadContainer
            && threadContainer.Item != threadMailItemViewModel)
        {
            threadContainer.Item = threadMailItemViewModel;
            threadContainer.IsThreadExpanded = threadMailItemViewModel.IsThreadExpanded;
        }
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        base.ClearContainerForItemOverride(element, item);

        if (item is MailItemViewModel mailItemViewModel && element is WinoMailItemViewModelListViewItem container)
        {
            container.Item = null;
        }
        else if (item is ThreadMailItemViewModel threadMailItemViewModel && element is WinoThreadMailItemViewModelListViewItem threadContainer)
        {
            threadContainer.Item = null;
            threadContainer.IsThreadExpanded = false;
        }
    }

    public WinoMailItemViewModelListViewItem? GetMailItemContainer(MailItemViewModel mailItemViewModel)
    {
        foreach (var item in Items)
        {
            if (item is MailItemViewModel mailItem && mailItem.Id == mailItemViewModel.Id) return ContainerFromItem(mailItemViewModel) as WinoMailItemViewModelListViewItem;
            if (item is ThreadMailItemViewModel threadMailItem && threadMailItem.GetContainingIds().Contains(mailItemViewModel.MailCopy.UniqueId))
            {
                var threadContainer = ContainerFromItem(threadMailItem) as WinoThreadMailItemViewModelListViewItem;

                // Try to get the inner WinoListView.
                if (threadContainer != null)
                {
                    var innerListViewControl = threadContainer.GetWinoListViewControl();

                    return innerListViewControl?.ContainerFromItem(mailItemViewModel) as WinoMailItemViewModelListViewItem;
                }
            }
        }

        return null;
    }

    public async Task<Tuple<WinoMailItemViewModelListViewItem?, WinoThreadMailItemViewModelListViewItem?, WinoListView?>> GetItemContainersAsync(MailItemViewModel mailItemViewModel)
    {
        WinoMailItemViewModelListViewItem? itemContainer = null;
        WinoThreadMailItemViewModelListViewItem? threadContainer = null;
        WinoListView? innerListView = null;

        int retryCount = 0;
        int maxRetries = 5;

        foreach (var item in Items)
        {
            if (item is MailItemViewModel mailItem && mailItem.Id == mailItemViewModel.Id)
            {
                itemContainer = ContainerFromItem(mailItemViewModel) as WinoMailItemViewModelListViewItem;

                // Not realized yet.
                if (itemContainer == null)
                {
                    ScrollIntoView(mailItemViewModel);

                    // Wait for the container to be generated.
                    while (itemContainer == null && retryCount < maxRetries)
                    {
                        await Task.Delay(100); // Wait a bit for the UI to update
                        itemContainer = ContainerFromItem(mailItemViewModel) as WinoMailItemViewModelListViewItem;
                        retryCount++;
                    }
                }

                break;
            }
            else if (item is ThreadMailItemViewModel threadMailItemViewModel && threadMailItemViewModel.HasUniqueId(mailItemViewModel.MailCopy.UniqueId))
            {
                threadContainer = ContainerFromItem(threadMailItemViewModel) as WinoThreadMailItemViewModelListViewItem;

                if (threadContainer == null)
                {
                    ScrollIntoView(threadMailItemViewModel);

                    while (threadContainer == null && retryCount < maxRetries)
                    {
                        await Task.Delay(100); // Wait a bit for the UI to update
                        threadContainer = ContainerFromItem(threadMailItemViewModel) as WinoThreadMailItemViewModelListViewItem;
                        retryCount++;
                    }
                }

                // Try to get the inner WinoListView.
                if (threadContainer != null)
                {
                    threadContainer.IsThreadExpanded = true;

                    var innerListViewControl = threadContainer.GetWinoListViewControl();

                    if (innerListViewControl != null)
                    {
                        innerListView = innerListViewControl;

                        itemContainer = innerListViewControl.ContainerFromItem(mailItemViewModel) as WinoMailItemViewModelListViewItem;

                        // Item thread has been found but container is not realized yet.
                        // This could happen when Sent item passed to navigate for Inbox or vice-versa.
                        // Ideally, we should select the first UniqueId match in the thread in this case.

                        if (itemContainer == null)
                        {
                            var realThreadItem = innerListViewControl.Items.Cast<MailItemViewModel>().FirstOrDefault(a => a.UniqueId == mailItemViewModel.MailCopy.UniqueId);

                            if (realThreadItem != null)
                            {
                                itemContainer = innerListViewControl.ContainerFromItem(realThreadItem) as WinoMailItemViewModelListViewItem;
                            }
                        }
                    }
                }
                break;
            }
        }

        return new Tuple<WinoMailItemViewModelListViewItem?, WinoThreadMailItemViewModelListViewItem?, WinoListView?>(itemContainer, threadContainer, innerListView);
    }

    public void ChangeSelectionMode(ListViewSelectionMode mode)
    {
        // Not only this control, but also all inner WinoListView controls should change the selection mode.
        // TODO: New threads added after this call won't have the correct selection mode.

        SelectionMode = mode;

        foreach (var item in Items)
        {
            if (item is ThreadMailItemViewModel)
            {
                var itemContainer = ContainerFromItem(item) as WinoThreadMailItemViewModelListViewItem;
                if (itemContainer != null)
                {
                    var innerListViewControl = itemContainer.GetWinoListViewControl();
                    innerListViewControl?.ChangeSelectionMode(mode);
                }
            }
        }
    }

    public void Cleanup()
    {
        DragItemsStarting -= ItemDragStarting;
        DragItemsCompleted -= ItemDragCompleted;

        if (internalScrollviewer != null)
        {
            internalScrollviewer.ViewChanged -= InternalScrollVeiwerViewChanged;
        }
    }

    private void ItemDragStarting(object sender, DragItemsStartingEventArgs args)
    {
        // Dragging multiple mails from different accounts/folders are supported with the condition below:
        // All mails belongs to the drag will be matched on the dropped folder's account.
        // Meaning that if users drag 1 mail from Account A/Inbox and 1 mail from Account B/Inbox,
        // and drop to Account A/Inbox, the mail from Account B/Inbox will NOT be moved.

        var itemsToDrag = ResolveDraggedMailItems(args);

        if (itemsToDrag.Count == 0)
        {
            return;
        }

        var dragPackage = new MailDragPackage(itemsToDrag.Cast<object>());
        args.Data.Properties.Add(nameof(MailDragPackage), dragPackage);

        var draggingText = string.Format(Translator.MailsDragging, itemsToDrag.Count);
        args.Data.SetText(draggingText);
        args.Data.Properties.Title = draggingText;
        // args.DragUI.SetContentFromDataPackage();

        MailDragStateChanged?.Invoke(this, new MailDragStateChangedEventArgs(true, itemsToDrag.Count));
    }

    private void ItemDragCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        MailDragStateChanged?.Invoke(this, new MailDragStateChangedEventArgs(false, 0));
    }

    private List<MailItemViewModel> ResolveDraggedMailItems(DragItemsStartingEventArgs args)
    {
        var draggedItems = ExpandDragItems(args.Items.Cast<object>());
        var selectedItems = GetSelectedMailItemsFromCurrentList();

        if (selectedItems.Count > 1)
        {
            var selectedIds = selectedItems.Select(a => a.UniqueId).ToHashSet();
            bool dragStartedFromSelection = draggedItems.Any(a => selectedIds.Contains(a.UniqueId));

            if (dragStartedFromSelection)
            {
                return selectedItems;
            }
        }

        return draggedItems.Count > 0 ? draggedItems : selectedItems;
    }

    private List<MailItemViewModel> GetSelectedMailItemsFromCurrentList()
    {
        if (IsThreadListView)
        {
            return Items
                .Cast<object>()
                .OfType<MailItemViewModel>()
                .Where(a => a.IsSelected)
                .GroupBy(a => a.UniqueId)
                .Select(a => a.First())
                .ToList();
        }

        return Items
            .Cast<object>()
            .OfType<IMailListItem>()
            .SelectMany(a => a.GetSelectedMailItems())
            .GroupBy(a => a.UniqueId)
            .Select(a => a.First())
            .ToList();
    }

    private static List<MailItemViewModel> ExpandDragItems(IEnumerable<object> dragItems)
    {
        var result = new List<MailItemViewModel>();

        foreach (var dragItem in dragItems)
        {
            if (dragItem is MailItemViewModel mailItem)
            {
                result.Add(mailItem);
            }
            else if (dragItem is ThreadMailItemViewModel threadItem)
            {
                result.AddRange(threadItem.ThreadEmails);
            }
            else if (dragItem is IMailListItem mailListItem)
            {
                result.AddRange(mailListItem.GetSelectedMailItems());
            }
        }

        return result
            .GroupBy(a => a.UniqueId)
            .Select(a => a.First())
            .ToList();
    }
}

public sealed class MailDragStateChangedEventArgs(bool isDragging, int draggedItemCount) : EventArgs
{
    public bool IsDragging { get; } = isDragging;
    public int DraggedItemCount { get; } = draggedItemCount;
}
