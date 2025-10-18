using System.Collections.Generic;
using System.Windows.Input;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Mail.WinUI.Controls.Advanced;

public partial class WinoItemsView : ItemsView
{
    private const string PART_ScrollView = nameof(PART_ScrollView);

    private ScrollView? _internalScrollView;

    [GeneratedDependencyProperty]
    public partial ICommand LoadMoreCommand { get; set; }

    public IEnumerable<object>? CastedItemsSource => ItemsSource as IEnumerable<object>;

    public WinoItemsView()
    {
        DefaultStyleKey = typeof(ItemsView);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _internalScrollView = GetTemplateChild("PART_ScrollView") as ScrollView ?? throw new System.Exception("Can't find the ScrollView in WinoItemsView.");

        _internalScrollView.ViewChanged -= InternalScrollViewPositionChanged;
        _internalScrollView.ViewChanged += InternalScrollViewPositionChanged;
    }

    private void InternalScrollViewPositionChanged(ScrollView sender, object args)
    {
        if (_internalScrollView == null) return;

        // No need to raise init request if there are no items in the list.
        if (ItemsSource == null) return;

        double progress = sender.VerticalOffset / sender.ScrollableHeight;

        // Trigger when scrolled past 90% of total height
        if (progress >= 0.9) LoadMoreCommand?.Execute(null);
    }
}
