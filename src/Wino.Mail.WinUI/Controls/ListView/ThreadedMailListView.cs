using Microsoft.UI.Xaml.Input;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using Wino.Mail.Controls.Core;
using Wino.Helpers;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.MailItem;
using global::Wino.Mail.Controls.MailListView;
using Wino.Mail.ViewModels.Data;
using WinRT;

namespace Wino.Mail.WinUI.Controls.ListView;

/// <summary>
/// Wino-specific host around the reusable list control. Domain drag payloads,
/// and commands stay in the app. Selection remains owned by the reusable control
/// and is exposed as a single stable snapshot.
/// </summary>
public sealed partial class ThreadedMailListView : WinoMailListView
{
    private const string ScrollViewerPartName = "ScrollViewer";
    private ScrollViewer? _scrollViewer;
    private PointerEventHandler? _pointerWheelHandler;

    public static readonly DependencyProperty LoadMoreCommandProperty = DependencyProperty.Register(
        nameof(LoadMoreCommand),
        typeof(ICommand),
        typeof(ThreadedMailListView),
        new PropertyMetadata(null));

    public static readonly DependencyProperty GroupHeaderTemplateSelectorProperty = DependencyProperty.Register(
        nameof(GroupHeaderTemplateSelector),
        typeof(DataTemplateSelector),
        typeof(ThreadedMailListView),
        new PropertyMetadata(null, OnGroupHeaderTemplateSelectorChanged));

    public ICommand? LoadMoreCommand
    {
        get => (ICommand?)GetValue(LoadMoreCommandProperty);
        set => SetValue(LoadMoreCommandProperty, value);
    }

    public DataTemplateSelector? GroupHeaderTemplateSelector
    {
        [DynamicWindowsRuntimeCast(typeof(DataTemplateSelector))]
        get => (DataTemplateSelector?)GetValue(GroupHeaderTemplateSelectorProperty);
        set => SetValue(GroupHeaderTemplateSelectorProperty, value);
    }

    public event EventHandler<MailDragStateChangedEventArgs>? MailDragStateChanged;

    public override string GetRowAutomationName(MailListRow row)
    {
        var source = row.IsThreadHead ? row.Thread?.RepresentativeItem ?? row.SourceItem : row.SourceItem;
        if (source is not MailItemViewModel mail) return base.GetRowAutomationName(row);

        var culture = CultureInfo.DefaultThreadCurrentUICulture ?? CultureInfo.CurrentUICulture;
        var mails = row.LeafItems.OfType<MailItemViewModel>().ToList();
        var details = new List<string>
        {
            mail.NameSortKey,
            XamlHelpers.GetMailItemSubjectForListing(mail.Subject),
            XamlHelpers.GetCreationDateString(mail.CreationDate, XamlHelpers.CurrentMailTimeFormatPreference),
            mails.Any(item => !item.IsRead) ? Translator.Accessibility_MailItemReadState_Unread : Translator.Accessibility_MailItemReadState_Read
        };
        if (mails.Any(item => item.IsFlagged)) details.Add(Translator.Accessibility_MailItemFlagged);
        if (mails.Any(item => item.HasAttachments)) details.Add(Translator.Accessibility_MailItemHasAttachments);
        if (row.IsThreadHead && row.Thread is { } thread)
        {
            details.Add(string.Format(culture, Translator.Accessibility_MailThreadMessageCount, thread.Count));
            details.Add(thread.IsExpanded ? Translator.Accessibility_MailThreadExpanded : Translator.Accessibility_MailThreadCollapsed);
        }
        return string.Join(", ", details.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    [DynamicWindowsRuntimeCast(typeof(ScrollViewer))]
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        DragItemsStarting -= OnDragItemsStarting;
        DragItemsStarting += OnDragItemsStarting;
        DragItemsCompleted -= OnDragItemsCompleted;
        DragItemsCompleted += OnDragItemsCompleted;

        if (_scrollViewer is not null)
        {
            _scrollViewer.ViewChanged -= OnScrollViewerViewChanged;
        }

        _scrollViewer = GetTemplateChild(ScrollViewerPartName) as ScrollViewer;
        if (_scrollViewer is not null)
        {
            _scrollViewer.ViewChanged += OnScrollViewerViewChanged;
        }

        ApplyGroupHeaderTemplateSelector();

        if (_pointerWheelHandler is not null)
        {
            RemoveHandler(PointerWheelChangedEvent, _pointerWheelHandler);
        }
        _pointerWheelHandler = new PointerEventHandler(OnPointerWheelChangedInternal);
        AddHandler(PointerWheelChangedEvent, _pointerWheelHandler, handledEventsToo: true);
    }

    public override void Cleanup()
    {
        DragItemsStarting -= OnDragItemsStarting;
        DragItemsCompleted -= OnDragItemsCompleted;
        if (_scrollViewer is not null)
        {
            _scrollViewer.ViewChanged -= OnScrollViewerViewChanged;
        }

        base.Cleanup();
    }

    private static void OnGroupHeaderTemplateSelectorChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        ((ThreadedMailListView)sender).ApplyGroupHeaderTemplateSelector();
    }

    private void ApplyGroupHeaderTemplateSelector()
    {
        if (GroupHeaderTemplateSelector is null)
        {
            return;
        }

        GroupStyle.Clear();
        GroupStyle.Add(new GroupStyle
        {
            HidesIfEmpty = true,
            HeaderTemplateSelector = GroupHeaderTemplateSelector,
        });
    }

    private void OnScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (_scrollViewer is null || _scrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        var progress = _scrollViewer.VerticalOffset / _scrollViewer.ScrollableHeight;
        if (progress >= 0.9 && LoadMoreCommand?.CanExecute(null) == true)
        {
            LoadMoreCommand.Execute(null);
        }
    }

    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs args)
    {
        var selectedMails = ResolveDraggedMails(args);
        if (selectedMails.Count == 0)
        {
            return;
        }

        var dragPackage = new MailDragPackage(selectedMails.Cast<object>());
        args.Data.Properties.Add(nameof(MailDragPackage), dragPackage);
        var text = string.Format(Translator.MailsDragging, selectedMails.Count);
        args.Data.SetText(text);
        args.Data.Properties.Title = text;
        MailDragStateChanged?.Invoke(this, new(true, selectedMails.Count));
    }

    /// <summary>
    /// Dragging a row does not select it, so the selection only describes the drag when the
    /// dragged rows are part of it. Otherwise the dragged rows are the payload; without this a
    /// drag that starts on an unselected mail carries nothing and every folder refuses the drop.
    /// </summary>
    private List<MailItemViewModel> ResolveDraggedMails(DragItemsStartingEventArgs args)
    {
        var draggedMails = Distinct(args.Items
            .OfType<MailListRow>()
            .SelectMany(static row => row.LeafItems)
            .OfType<MailItemViewModel>());
        var selectedMails = Distinct(SelectedMailItems.OfType<MailItemViewModel>());

        if (draggedMails.Count == 0)
        {
            return selectedMails;
        }

        var selectedIds = selectedMails.Select(static item => item.UniqueId).ToHashSet();
        return draggedMails.Any(item => selectedIds.Contains(item.UniqueId)) ? selectedMails : draggedMails;

        static List<MailItemViewModel> Distinct(IEnumerable<MailItemViewModel> mails) =>
            mails.GroupBy(static item => item.UniqueId).Select(static group => group.First()).ToList();
    }

    private void OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        MailDragStateChanged?.Invoke(this, new(false, 0));

    protected override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        base.OnPointerEntered(e);
        if (FocusState == FocusState.Unfocused && XamlRoot is not null)
        {
            var focused = FocusManager.GetFocusedElement(XamlRoot);
            if (focused is not TextBox && focused is not RichEditBox)
            {
                Focus(FocusState.Pointer);
            }
        }
    }

    private void OnPointerWheelChangedInternal(object sender, PointerRoutedEventArgs e)
    {
        if (_scrollViewer is null || _scrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control))
        {
            return;
        }

        var pointerPoint = e.GetCurrentPoint(this);
        if (pointerPoint.Properties.IsHorizontalMouseWheel)
        {
            return;
        }

        int wheelDelta = pointerPoint.Properties.MouseWheelDelta;
        if (wheelDelta != 0)
        {
            const double scrollFactor = 0.6;
            double targetOffset = _scrollViewer.VerticalOffset - (wheelDelta * scrollFactor);
            targetOffset = Math.Clamp(targetOffset, 0, _scrollViewer.ScrollableHeight);

            _scrollViewer.ChangeView(null, targetOffset, null, disableAnimation: false);
            e.Handled = true;
        }
    }
}
