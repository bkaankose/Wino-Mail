using System;
using System.Collections.Generic;
using System.Windows.Input;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.WinUI.Controls.Advanced;

[Obsolete("ItemsView sucks. Hard to deal with virtualization issues. Use ListView. This control is here to wise up anyone who tries to use it.")]
public partial class WinoItemsView : ItemsView
{
    private const string PART_ScrollView = nameof(PART_ScrollView);

    private ScrollView? _internalScrollView;

    [GeneratedDependencyProperty]
    public partial ICommand? LoadMoreCommand { get; set; }

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

    public bool SelectMailItemContainer(MailItemViewModel mailItemViewModel)
    {
        return true;
    }

    /// <summary>
    /// Recursively clears all selections except the given mail.
    /// </summary>
    /// <param name="exceptViewModel">Exceptional mail item to be not unselected.</param>
    /// <param name="preserveThreadExpanding">Whether expansion states of thread containers should stay as it is or not.</param>
    public void ClearSelections(MailItemViewModel? exceptViewModel = null, bool preserveThreadExpanding = false)
    {
        if (CastedItemsSource == null) return;

        foreach (var item in CastedItemsSource)
        {
            if (item is MailItemViewModel mailItemViewModel)
            {
                if (mailItemViewModel != exceptViewModel)
                {
                    mailItemViewModel.IsSelected = false;
                }
            }
            else if (item is ThreadMailItemViewModel threadMailItemViewModel)
            {
                threadMailItemViewModel.IsSelected = false;

                if (!preserveThreadExpanding) threadMailItemViewModel.IsThreadExpanded = false;

                foreach (var childMail in threadMailItemViewModel.ThreadEmails)
                {
                    if (childMail != exceptViewModel)
                    {
                        childMail.IsSelected = false;
                    }
                }
            }
        }
    }
}
