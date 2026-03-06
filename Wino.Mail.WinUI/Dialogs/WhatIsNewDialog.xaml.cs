using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Updates;

namespace Wino.Dialogs;

public sealed partial class WhatIsNewDialog : ContentDialog
{
    private readonly IUpdateManager _updateManager;

    public List<UpdateNoteSection> Sections { get; }

    private bool _canClose = false;

    public WhatIsNewDialog(UpdateNotes notes, IUpdateManager updateManager)
    {
        InitializeComponent();

        _updateManager = updateManager;
        Sections = notes.Sections;

        // Show the Get Started button immediately when there is only one page.
        UpdateNotesControl.SelectedIndexChanged += OnUpdateSectionChanged;
        UpdateGetStartedButtonVisibility(UpdateNotesControl.SelectedIndex);
        Closing += OnDialogClosing;
    }

    private void OnUpdateSectionChanged(object? sender, int selectedIndex)
        => UpdateGetStartedButtonVisibility(selectedIndex);

    private void UpdateGetStartedButtonVisibility(int selectedIndex)
    {
        GetStartedButton.Visibility = selectedIndex == Sections.Count - 1
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnDialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        // Only allow closing when Get Started button was clicked.
        if (!_canClose)
            args.Cancel = true;
    }

    private void OnGetStartedClicked(object sender, RoutedEventArgs e)
    {
        GetStartedButton.IsEnabled = false;
        _updateManager.MarkUpdateNotesAsSeen();
        _canClose = true;
        Hide();
    }
}
