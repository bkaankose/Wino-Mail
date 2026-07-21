using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Windows.System;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Editor;
using Wino.Mail.Uwp;
using Wino.Mail.Uwp.Views.Abstract;
using Wino.Messaging.Client.Calendar;
using Wino.Messaging.Client.Shell;

namespace Wino.Calendar.Views;

public sealed partial class EventDetailsPage : EventDetailsPageAbstract,
    IRecipient<ApplicationThemeChanged>,
    IRecipient<CalendarDescriptionRenderingRequested>
{
    private readonly IPreferencesService _preferencesService = App.Current.Services.GetService<IPreferencesService>()!;

    public EventDetailsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        EventDetailsWebView.NavigationRequested -= EventDetailsWebView_NavigationRequested;
        EventDetailsWebView.NavigationRequested += EventDetailsWebView_NavigationRequested;
        _ = InitializeAndRenderAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        EventDetailsWebView.NavigationRequested -= EventDetailsWebView_NavigationRequested;
        EventDetailsWebView.Dispose();
    }

    private async Task InitializeAndRenderAsync()
    {
        await EventDetailsWebView.InitializeAsync();
        await RenderDescriptionAsync();
    }

    private async Task RenderDescriptionAsync()
    {
        if (DispatcherQueue != null && !DispatcherQueue.HasThreadAccess)
        {
            await DispatcherQueue.EnqueueAsync(RenderDescriptionAsync);
            return;
        }

        if (ViewModel?.CurrentEvent?.CalendarItem == null) return;

        EventDetailsWebView.IsDarkMode = ViewModel.IsDarkWebviewRenderer;
        await EventDetailsWebView.SetReaderTypographyAsync(
            $"{_preferencesService.ReaderFont}, sans-serif",
            _preferencesService.ReaderFontSize);
        await EventDetailsWebView.RenderHtmlAsync(
            ViewModel.CurrentEvent.CalendarItem.Description ?? " ",
            shouldLinkify: true);
    }

    private async void EventDetailsWebView_NavigationRequested(object? sender, RendererNavigationRequestedEventArgs args)
    {
        try { await Launcher.LaunchUriAsync(args.Uri); } catch (Exception) { }
    }

    void IRecipient<ApplicationThemeChanged>.Receive(ApplicationThemeChanged message)
    {
        _ = DispatcherQueue.EnqueueAsync(async () =>
        {
            ViewModel.IsDarkWebviewRenderer = message.IsUnderlyingThemeDark;
            EventDetailsWebView.IsDarkMode = message.IsUnderlyingThemeDark;
            await EventDetailsWebView.InitializeAsync();
        });
    }

    void IRecipient<CalendarDescriptionRenderingRequested>.Receive(CalendarDescriptionRenderingRequested message)
        => _ = RenderDescriptionAsync();

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();
        WeakReferenceMessenger.Default.Register<ApplicationThemeChanged>(this);
        WeakReferenceMessenger.Default.Register<CalendarDescriptionRenderingRequested>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();
        WeakReferenceMessenger.Default.Unregister<ApplicationThemeChanged>(this);
        WeakReferenceMessenger.Default.Unregister<CalendarDescriptionRenderingRequested>(this);
    }

    private void AttachmentClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CalendarAttachmentViewModel attachmentViewModel)
            ViewModel?.OpenAttachmentCommand.Execute(attachmentViewModel);
    }

    private void OpenCalendarAttachment_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.CommandParameter is CalendarAttachmentViewModel attachment)
            ViewModel?.OpenAttachmentCommand.Execute(attachment);
    }

    private void SaveCalendarAttachment_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.CommandParameter is CalendarAttachmentViewModel attachment)
            ViewModel?.SaveAttachmentCommand.Execute(attachment);
    }
}
