using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Playground.Models;
using Wino.Mail.Controls.Playground.ViewModels;

namespace Wino.Mail.Controls.Playground.Pages;

public sealed partial class MailListInteractionsPage : Page, IDisposable
{
    private ScrollViewer? _scrollViewer;
    private bool _disposed;

    public MailListInteractionsPageViewModel ViewModel { get; } = new();

    public MailListInteractionsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        ViewModel.Items.CollectionChanged += OnItemsCollectionChanged;
        RefreshInspector();
    }

    private void OnItemsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Rows are projected after the batch completes; read the counters on the next pass.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, RefreshInspector);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _scrollViewer = FindDescendant<ScrollViewer>(MailList);
        if (_scrollViewer is not null)
        {
            _scrollViewer.ViewChanged += OnScrollViewChanged;
        }

        RefreshInspector();
    }

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => RefreshCounters();

    private void OnSelectionSnapshotChanged(object? sender, MailListSelectionSnapshot e) => RefreshInspector();

    private void OnThreadExpansionChanged(object? sender, ThreadExpansionChangedEventArgs e) => RefreshInspector();

    // ---- Projection ----

    private void SortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortCombo.SelectedItem is ComboBoxItem { Tag: MailListSortMode mode })
        {
            ViewModel.SortMode = mode;
        }
    }

    private void GroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupCombo.SelectedItem is ComboBoxItem { Tag: MailListGroupMode mode })
        {
            ViewModel.GroupMode = mode;
        }
    }

    // ---- Single item ----

    private void AddSingle_Click(object sender, RoutedEventArgs e) => ViewModel.AddSingle();

    private void RemoveSelected_Click(object sender, RoutedEventArgs e) =>
        ViewModel.Remove(MailList.SelectedMailItems.Select(static item => item.StableId).ToArray());

    private void UpdateSubject_Click(object sender, RoutedEventArgs e) => WithActiveItem(ViewModel.UpdateSubject);

    private void MoveDay_Click(object sender, RoutedEventArgs e) => WithActiveItem(ViewModel.MoveToPreviousDay);

    private void RenameSender_Click(object sender, RoutedEventArgs e) => WithActiveItem(ViewModel.RenameSender);

    private void TogglePin_Click(object sender, RoutedEventArgs e) => WithActiveItem(ViewModel.TogglePin);

    private void ToggleRead_Click(object sender, RoutedEventArgs e) => WithActiveItem(ViewModel.ToggleRead);

    // ---- Threads ----

    private void AddThread_Click(object sender, RoutedEventArgs e) => ViewModel.AddThread();

    private void RemoveThread_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RemoveThreads(MailList.SelectedMailItems
            .OfType<MailListLabItem>()
            .Select(static item => item.ThreadId)
            .Where(static id => id is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray());

    private void AddReply_Click(object sender, RoutedEventArgs e) => WithActiveItem(item => ViewModel.AddReply(item, newer: true));

    private void AddOlderReply_Click(object sender, RoutedEventArgs e) => WithActiveItem(item => ViewModel.AddReply(item, newer: false));

    // ---- Bulk ----

    private void BulkAdd_Click(object sender, RoutedEventArgs e) => ViewModel.BulkAdd(40, 10);

    private void RemoveEveryOther_Click(object sender, RoutedEventArgs e) => ViewModel.RemoveEveryOther();

    private void RemoveOldest_Click(object sender, RoutedEventArgs e) => ViewModel.RemoveOldest(20);

    private void Reset_Click(object sender, RoutedEventArgs e) => ViewModel.Reset();

    private void Clear_Click(object sender, RoutedEventArgs e) => ViewModel.Clear();

    // ---- Inspector ----

    private void WithActiveItem(Action<MailListLabItem> action)
    {
        // A selected leaf hidden in a collapsed thread has no native row, so the snapshot
        // reports no active item; the identity token still names it.
        var snapshot = MailList.SelectionSnapshot;
        var target = snapshot.ActiveItem as MailListLabItem ?? snapshot.SelectedItems.OfType<MailListLabItem>().LastOrDefault();
        if (target is not null)
        {
            action(target);
        }

        // In-place updates do not change the selection, so refresh the inspector by hand.
        RefreshInspector();
    }

    private void DeleteAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        RemoveSelected_Click(sender, new RoutedEventArgs());
    }

    private void RefreshInspector()
    {
        if (MailList is null || _disposed)
        {
            return;
        }

        var snapshot = MailList.SelectionSnapshot;
        ActiveItemText.Text = snapshot.ActiveItem is MailListLabItem active
            ? Describe(active)
            : "(none)";
        var selected = snapshot.SelectedItems.OfType<MailListLabItem>().Select(Describe).ToArray();
        SelectionText.Text = selected.Length == 0 ? "(none)" : string.Join(Environment.NewLine, selected);
        ExpandedText.Text = MailList.ExpandedThreadKeys.Count == 0
            ? "(none)"
            : string.Join(", ", MailList.ExpandedThreadKeys);
        RefreshCounters();
    }

    private void RefreshCounters()
    {
        RowCountText.Text = $"{MailList.Items.Count} rows";
        ScrollOffsetText.Text = _scrollViewer is null
            ? "offset n/a"
            : $"offset {_scrollViewer.VerticalOffset.ToString("0.#", CultureInfo.InvariantCulture)}";
    }

    private static string Describe(MailListLabItem item) => $"{item.ThreadId} · {item.Sender} · {item.Subject}";

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ViewModel.Items.CollectionChanged -= OnItemsCollectionChanged;
        if (_scrollViewer is not null)
        {
            _scrollViewer.ViewChanged -= OnScrollViewChanged;
            _scrollViewer = null;
        }

        Bindings.StopTracking();
        MailList.Dispose();
        GC.SuppressFinalize(this);
    }
}
