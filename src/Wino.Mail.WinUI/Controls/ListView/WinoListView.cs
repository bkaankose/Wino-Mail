using System;
using System.Collections.Generic;
using System.Linq;
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
    public partial ICommand? LoadMoreCommand { get; set; }

    [GeneratedDependencyProperty]
    public partial DataTemplateSelector? GroupHeaderTemplateSelector { get; set; }

    public event EventHandler<MailDragStateChangedEventArgs>? MailDragStateChanged;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        DragItemsStarting -= ItemDragStarting;
        DragItemsStarting += ItemDragStarting;
        DragItemsCompleted -= ItemDragCompleted;
        DragItemsCompleted += ItemDragCompleted;

        internalScrollviewer = GetTemplateChild(PART_ScrollViewer) as ScrollViewer;

        ApplyGroupHeaderTemplateSelector();

        if (internalScrollviewer == null) return;

        internalScrollviewer.ViewChanged -= InternalScrollVeiwerViewChanged;
        internalScrollviewer.ViewChanged += InternalScrollVeiwerViewChanged;
    }

    partial void OnGroupHeaderTemplateSelectorPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        ApplyGroupHeaderTemplateSelector();
    }

    private void ApplyGroupHeaderTemplateSelector()
    {
        if (GroupHeaderTemplateSelector == null)
        {
            return;
        }

        var groupStyle = GroupStyle.FirstOrDefault();

        if (groupStyle == null)
        {
            groupStyle = new GroupStyle
            {
                HidesIfEmpty = true
            };

            GroupStyle.Add(groupStyle);
        }

        groupStyle.HeaderTemplate = null;
        groupStyle.HeaderTemplateSelector = GroupHeaderTemplateSelector;
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
