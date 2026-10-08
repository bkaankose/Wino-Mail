using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.WhatsNew;
using Wino.Mail.WinUI.Controls;
using Wino.Views.Abstract;

namespace Wino.Views.Misc;

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
                item.Icon = new WinoFontIcon { Icon = WinoIconGlyph.StarFilled };
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
