using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Serilog;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Editor;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Views.Abstract;
using Wino.Messaging.Client.Calendar;
using Wino.Messaging.Client.Shell;

namespace Wino.Calendar.Views;

public sealed partial class EventDetailsPage : EventDetailsPageAbstract,
    IRecipient<ApplicationThemeChanged>,
    IRecipient<CalendarDescriptionRenderingRequested>
{
    private readonly IPreferencesService _preferencesService = App.Current.Services.GetService<IPreferencesService>()!;
    public EventDetailsPage()
    {
        InitializeComponent();

    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _ = InitializeAndRenderAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        EventDetailsRenderer.Dispose();
    }

    private async Task InitializeAndRenderAsync()
    {
        try
        {
            await EventDetailsRenderer.InitializeAsync();
            await RenderDescriptionAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Calendar description WebView2 initialization failed.");
        }
    }

    private async Task RenderDescriptionAsync()
    {
        if (DispatcherQueue != null && !DispatcherQueue.HasThreadAccess)
        {
            await DispatcherQueue.EnqueueAsync(RenderDescriptionAsync);
            return;
        }

        if (ViewModel?.CurrentEvent?.CalendarItem == null)
            return;

        await UpdateEditorThemeAsync();
        await UpdateReaderFontPropertiesAsync();

        var description = ViewModel.CurrentEvent.CalendarItem.Description ?? string.Empty;
        await EventDetailsRenderer.RenderHtmlAsync(string.IsNullOrEmpty(description) ? " " : description);
    }

    private async void EventDetailsRenderer_NavigationRequested(object? sender, RendererNavigationRequestedEventArgs args)
    {
        try
        {
            await ViewModel.NativeAppService.LaunchUriAsync(args.Uri);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open a link from the calendar description renderer.");
        }
    }

    private void EventDetailsRenderer_InitializationFailed(object? sender, Exception exception)
        => Log.Error(exception, "Calendar description WebView2 initialization failed.");

    private async Task UpdateEditorThemeAsync()
    {
        if (DispatcherQueue != null && !DispatcherQueue.HasThreadAccess)
        {
            await DispatcherQueue.EnqueueAsync(UpdateEditorThemeAsync);
            return;
        }

        EventDetailsRenderer.IsDarkMode = ViewModel.IsDarkWebviewRenderer;
        await EventDetailsRenderer.InitializeAsync();
    }

    private async Task UpdateReaderFontPropertiesAsync()
    {
        var fontName = $"{_preferencesService.ReaderFont}, sans-serif";
        await EventDetailsRenderer.SetReaderTypographyAsync(fontName, _preferencesService.ReaderFontSize);
    }

    void IRecipient<ApplicationThemeChanged>.Receive(ApplicationThemeChanged message)
    {
        ViewModel.IsDarkWebviewRenderer = message.IsUnderlyingThemeDark;
        _ = UpdateEditorThemeAsync();
    }

    void IRecipient<CalendarDescriptionRenderingRequested>.Receive(CalendarDescriptionRenderingRequested message)
    {
        _ = RenderDescriptionAsync();
    }

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
        {
            ViewModel?.OpenAttachmentCommand.Execute(attachmentViewModel);
        }
    }

    private void OpenCalendarAttachment_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.CommandParameter is CalendarAttachmentViewModel attachment)
        {
            ViewModel?.OpenAttachmentCommand.Execute(attachment);
        }
    }

    private void SaveCalendarAttachment_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.CommandParameter is CalendarAttachmentViewModel attachment)
        {
            ViewModel?.SaveAttachmentCommand.Execute(attachment);
        }
    }
}
