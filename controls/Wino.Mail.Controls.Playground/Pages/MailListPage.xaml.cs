using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Core.HoverActions;
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

    public HoverActionLabels HoverActionLabels { get; } =
        new("Archive", "Delete", "Flag / Unflag", "Read / Unread", "Move to Junk");

    public ICommand InvokeHoverActionCommand { get; }

    public MailListPage()
    {
        InvokeHoverActionCommand = new RelayCommand<HoverActionCommandRequest>(InvokeHoverAction);

        InitializeComponent();
        ViewModel.Items.CollectionChanged += ItemsCollectionChanged;
        RefreshSelectionInspector();
    }

    private void InvokeHoverAction(HoverActionCommandRequest? request)
    {
        if (request is null)
        {
            return;
        }

        HoverActionStatusText.Text = $"Invoked {request.Action} for {request.Row.SourceItem.NameSortKey}";
    }

    private void HoverAnimationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HoverAnimationCombo.SelectedItem is ComboBoxItem { Tag: HoverActionAnimation animation })
        {
            MailList.HoverActionAnimation = animation;
        }
    }

    private void HoverPositionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HoverPositionCombo.SelectedItem is ComboBoxItem { Tag: HoverActionPosition position })
        {
            MailList.HoverActionPosition = position;
        }
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

    private void ApplyMetadata_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyMetadata();

    private void ReplaceMetadata_Click(object sender, RoutedEventArgs e) => ViewModel.ReplaceMetadata();

    private void ClearMetadata_Click(object sender, RoutedEventArgs e) => ViewModel.ClearMetadata();

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

    private void MultiSelectChecked(object sender, RoutedEventArgs e)
    {
        MailList.SelectionMode = ListViewSelectionMode.Multiple;
    }

    private void MultiSelectUnchecked(object sender, RoutedEventArgs e)
    {
        MailList.SelectionMode = ListViewSelectionMode.Extended;
    }
}
