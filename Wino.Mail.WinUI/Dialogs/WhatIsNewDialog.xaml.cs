using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Updates;

namespace Wino.Dialogs;

public sealed partial class WhatIsNewDialog : ContentDialog
{
    private readonly IUpdateManager _updateManager;

    public List<UpdateNoteSection> Sections { get; }

    public WhatIsNewDialog(UpdateNotes notes, IUpdateManager updateManager)
    {
        InitializeComponent();

        _updateManager = updateManager;
        Sections = notes.Sections;

        // Set the number of pages in the pip pager after sections are assigned.
        FlipViewPager.NumberOfPages = Sections.Count;

        // Show the Get Started button immediately when there is only one page.
        if (Sections.Count <= 1)
            GetStartedButton.Visibility = Visibility.Visible;
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        // Block ESC key to prevent accidental dismissal.
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void OnFlipViewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int selectedIndex = UpdateFlipView.SelectedIndex;

        // Keep pip pager in sync with the flip view.
        FlipViewPager.SelectedPageIndex = selectedIndex;

        // Show Get Started button only on the last page.
        GetStartedButton.Visibility = selectedIndex == Sections.Count - 1
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnPipsPagerSelectedIndexChanged(PipsPager sender, PipsPagerSelectedIndexChangedEventArgs args)
    {
        UpdateFlipView.SelectedIndex = sender.SelectedPageIndex;
    }

    private async void OnGetStartedClicked(object sender, RoutedEventArgs e)
    {
        GetStartedButton.IsEnabled = false;

        await _updateManager.RunPendingMigrationsAsync();
        _updateManager.MarkUpdateNotesAsSeen();

        Hide();
    }
}
