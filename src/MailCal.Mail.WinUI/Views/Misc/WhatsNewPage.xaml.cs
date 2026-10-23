using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.WhatsNew;
using MailCal.Mail.WinUI.Controls;
using MailCal.Views.Abstract;

namespace MailCal.Views.Misc;

public sealed partial class WhatsNewPage : WhatsNewPageAbstract
{
    public WhatsNewPage()
    {
        InitializeComponent();

        ViewModel.ReleasesLoaded += OnReleasesLoaded;
        Unloaded += OnPageUnloaded;
    }

    /// <summary>SelectorBar is not an ItemsControl, so its items are built from the loaded releases.</summary>
    private void OnReleasesLoaded(object? sender, EventArgs e)
    {
        VersionSelectorBar.Items.Clear();

        foreach (var release in ViewModel.Releases)
        {
            var item = new SelectorBarItem
            {
                Text = release.Version,
                Tag = release,
                IsSelected = release == ViewModel.SelectedRelease
            };

            if (release.IsStarred)
            {
                item.Icon = new MailCalFontIcon { Icon = MailCalIconGlyph.StarFilled };
            }

            AutomationProperties.SetAutomationId(item, $"WhatsNewVersion_{release.Version}");
            AutomationProperties.SetName(item, release.IsStarred
                ? string.Format(Translator.WhatsNew_StarredVersionAutomationName, release.Version)
                : release.Version);

            VersionSelectorBar.Items.Add(item);
        }
    }

    private void VersionSelectorBarSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is WhatsNewRelease release)
        {
            ViewModel.SelectedRelease = release;
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        Unloaded -= OnPageUnloaded;
        ViewModel.ReleasesLoaded -= OnReleasesLoaded;
    }
}
