using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Updates;

namespace Wino.Dialogs;

public sealed partial class WhatIsNewDialog : ContentDialog
{
    private readonly IUpdateManager _updateManager;
    private readonly UpdateNotes _notes;

    public List<UpdateNoteSection> Sections { get; }

    private bool _canClose = false;

    public WhatIsNewDialog(UpdateNotes notes, IUpdateManager updateManager)
    {
        InitializeComponent();

        _notes = notes;
        _updateManager = updateManager;
        Sections = notes.Sections;

        // Show the Get Started button immediately when there is only one page.
        UpdateNotesControl.SelectedIndexChanged += OnUpdateSectionChanged;
        UpdateGetStartedButtonVisibility(UpdateNotesControl.SelectedIndex);

        InitializeMigrationStatus();
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

    private void InitializeMigrationStatus()
    {
        if (!_notes.HasPendingMigrations ||
            string.IsNullOrWhiteSpace(_notes.Migration.TitleKey) ||
            string.IsNullOrWhiteSpace(_notes.Migration.DescriptionKey))
            return;

        MigrationTitleText.Text = Translator.GetTranslatedString(_notes.Migration.TitleKey);
        MigrationDescriptionText.Text = Translator.GetTranslatedString(_notes.Migration.DescriptionKey);
    }

    private void OnDialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        // Only allow closing when Get Started button was clicked.
        if (!_canClose)
            args.Cancel = true;
    }

    private async void OnGetStartedClicked(object sender, RoutedEventArgs e)
    {
        GetStartedButton.IsEnabled = false;
        ContinueAnywayButton.Visibility = Visibility.Collapsed;
        MigrationErrorText.Visibility = Visibility.Collapsed;

        if (_notes.HasPendingMigrations)
        {
            GetStartedButton.Content = Translator.WhatIsNew_PreparingForNewVersionButton;
            MigrationPanel.Visibility = Visibility.Visible;
            MigrationProgressBar.Visibility = Visibility.Visible;
        }

        try
        {
            await _updateManager.RunPendingMigrationsAsync();
            _updateManager.MarkUpdateNotesAsSeen();
        }
        catch (System.Exception ex)
        {
            MigrationProgressBar.Visibility = Visibility.Collapsed;
            MigrationErrorText.Text = string.Format(Translator.WhatIsNew_MigrationFailedMessage, ex.GetType().Name);
            MigrationErrorText.Visibility = Visibility.Visible;
            ContinueAnywayButton.Visibility = Visibility.Visible;
            GetStartedButton.IsEnabled = true;
            GetStartedButton.Content = Translator.WhatIsNew_GetStartedButton;
            return;
        }

        _canClose = true;
        Hide();
    }

    private void OnContinueAnywayClicked(object sender, RoutedEventArgs e)
    {
        _updateManager.MarkUpdateNotesAsSeen();
        _canClose = true;
        Hide();
    }
}
