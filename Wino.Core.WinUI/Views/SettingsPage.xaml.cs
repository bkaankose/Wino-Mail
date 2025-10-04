﻿using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using MoreLinq;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;
using Wino.Views.Abstract;
using Wino.Views.Settings;

namespace Wino.Views;

public sealed partial class SettingsPage : SettingsPageAbstract, IRecipient<BreadcrumbNavigationRequested>
{
    public ObservableCollection<BreadcrumbNavigationItemViewModel> PageHistory { get; set; } = [];

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Re-register the breadcrumb navigation handler after base.OnNavigatedTo unregisters all handlers
        WeakReferenceMessenger.Default.Register<BreadcrumbNavigationRequested>(this);

        SettingsFrame.Navigate(typeof(SettingOptionsPage), null, new SuppressNavigationTransitionInfo());

        var initialRequest = new BreadcrumbNavigationRequested(Translator.MenuSettings, WinoPage.SettingOptionsPage);
        PageHistory.Add(new BreadcrumbNavigationItemViewModel(initialRequest, true));

        if (e.Parameter is WinoPage parameterPage)
        {
            switch (parameterPage)
            {
                case WinoPage.AppPreferencesPage:
                    WeakReferenceMessenger.Default.Send(new BreadcrumbNavigationRequested(Translator.SettingsAppPreferences_Title, WinoPage.AppPreferencesPage));
                    break;
                case WinoPage.PersonalizationPage:
                    WeakReferenceMessenger.Default.Send(new BreadcrumbNavigationRequested(Translator.SettingsPersonalization_Title, WinoPage.PersonalizationPage));
                    break;
            }
        }
    }

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();

        // Update Settings header in breadcrumb.

        var settingsHeader = PageHistory.FirstOrDefault();

        if (settingsHeader == null) return;

        settingsHeader.Title = Translator.MenuSettings;
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        // Explicitly unregister our message handlers before base.OnNavigatingFrom calls UnregisterAll
        WeakReferenceMessenger.Default.Unregister<BreadcrumbNavigationRequested>(this);

        base.OnNavigatingFrom(e);
    }

    void IRecipient<BreadcrumbNavigationRequested>.Receive(BreadcrumbNavigationRequested message)
    {
        var pageType = ViewModel.NavigationService.GetPageType(message.PageType);

        if (pageType == null) return;

        SettingsFrame.Navigate(pageType, message.Parameter, new SlideNavigationTransitionInfo() { Effect = SlideNavigationTransitionEffect.FromRight });

        PageHistory.ForEach(a => a.IsActive = false);

        PageHistory.Add(new BreadcrumbNavigationItemViewModel(message, true));
    }

    private void BreadItemClicked(Microsoft.UI.Xaml.Controls.BreadcrumbBar sender, Microsoft.UI.Xaml.Controls.BreadcrumbBarItemClickedEventArgs args)
    {
        var clickedPageHistory = PageHistory[args.Index];
        var activeIndex = PageHistory.IndexOf(PageHistory.FirstOrDefault(a => a.IsActive));

        while (PageHistory.FirstOrDefault(a => a.IsActive) != clickedPageHistory)
        {
            SettingsFrame.GoBack(new SlideNavigationTransitionInfo() { Effect = SlideNavigationTransitionEffect.FromRight });
            PageHistory.RemoveAt(PageHistory.Count - 1);
            PageHistory[PageHistory.Count - 1].IsActive = true;
        }
    }
}
