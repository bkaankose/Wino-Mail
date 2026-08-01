using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Playground.Models;
using Wino.Mail.Controls.Playground.ViewModels;

namespace Wino.Mail.Controls.Playground.Pages;

public sealed partial class MailListPage : Page
{
    public MailListPageViewModel ViewModel { get; } = new();

    public ObservableCollection<string> SelectedRows { get; } = [];
    public ObservableCollection<string> SelectedMailItems { get; } = [];
    public ObservableCollection<string> SelectedThreads { get; } = [];
    public ObservableCollection<string> ExpandedThreads { get; } = [];

    public MailListPage()
    {
        InitializeComponent();
        ViewModel.Items.CollectionChanged += ItemsCollectionChanged;
        RefreshSelectionInspector();
    }

    private void OnSelectionSnapshotChanged(object? sender, MailListSelectionSnapshot e) => RefreshSelectionInspector();

    private void OnThreadExpansionChanged(object? sender, ThreadExpansionChangedEventArgs e) => RefreshSelectionInspector();

    private void ItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshSelectionInspector();

    private void RemoveSelectedMailItems_Click(object sender, RoutedEventArgs e)
    {
        var ids = MailList.SelectedMailItems.OfType<MailListPlaygroundItem>().Select(item => item.StableId).ToArray();
        ViewModel.Items.RemoveRangeById(ids);
    }

    private void RemoveSelectedThreads_Click(object sender, RoutedEventArgs e)
    {
        var threadIds = MailList.SelectedMailItems.OfType<MailListPlaygroundItem>().Select(item => item.ThreadId).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        ViewModel.Items.RemoveRangeById(ViewModel.Items.OfType<MailListPlaygroundItem>().Where(item => threadIds.Contains(item.ThreadId)).Select(item => item.StableId));
    }

    private void AddMail_Click(object sender, RoutedEventArgs e) => ViewModel.AddMail();

    private void AddToMyThread_Click(object sender, RoutedEventArgs e) => ViewModel.AddToMyThread();

    private void AddStandalone_Click(object sender, RoutedEventArgs e) => ViewModel.AddStandalone();

    private void Reset_Click(object sender, RoutedEventArgs e) => ViewModel.Reset();

    private void RefreshSelectionInspector()
    {
        if (MailList is null)
            return;

        var selectedItems = MailList.SelectedMailItems.OfType<MailListPlaygroundItem>().ToArray();
        RemoveSelectedMailItemsButton.IsEnabled = selectedItems.Length > 0;
        RemoveSelectedThreadsButton.IsEnabled = selectedItems.Length > 0;
        Replace(SelectedRows, MailList.SelectedItems.OfType<MailListRow>().Select(row => $"{row.Kind} · {row.ThreadKey}"));
        Replace(SelectedMailItems, selectedItems.Select(item => $"{item.Subject} · {item.Sender}"));
        Replace(SelectedThreads, MailList.SelectedThreadKeys);
        Replace(ExpandedThreads, MailList.ExpandedThreadKeys);
    }

    private static void Replace(ObservableCollection<string> target, IEnumerable<string> values)
    {
        target.Clear();
        var materialized = values.ToArray();
        if (materialized.Length == 0)
            target.Add("(none)");
        else
            foreach (var value in materialized) target.Add(value);
    }
}
