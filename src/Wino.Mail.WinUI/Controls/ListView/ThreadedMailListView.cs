using System;
using System.Linq;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.MailItem;
using global::Wino.Mail.Controls.MailListView;
using Wino.Mail.ViewModels.Data;

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
        get => (DataTemplateSelector?)GetValue(GroupHeaderTemplateSelectorProperty);
        set => SetValue(GroupHeaderTemplateSelectorProperty, value);
    }

    public event EventHandler<MailDragStateChangedEventArgs>? MailDragStateChanged;

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
        var selectedMails = SelectedMailItems
            .OfType<MailItemViewModel>()
            .GroupBy(static item => item.UniqueId)
            .Select(static group => group.First())
            .ToList();
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

    private void OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        MailDragStateChanged?.Invoke(this, new(false, 0));
}
