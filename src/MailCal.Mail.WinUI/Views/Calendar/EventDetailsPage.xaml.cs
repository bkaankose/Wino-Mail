using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Serilog;
using MailCal.Calendar.ViewModels.Data;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Interfaces;
using MailCal.Editor;
using MailCal.Helpers;
using MailCal.Mail.Controls.Core.ContextFlyout;
using MailCal.Mail.WinUI;
using MailCal.Mail.WinUI.Views.Abstract;
using MailCal.Messaging.Client.Calendar;
using MailCal.Messaging.Client.Shell;
using MailCal.Core.Domain.Enums;

namespace MailCal.Calendar.Views;

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

    protected override void ReleasePageResources()
        => ReleaseTemplateSelector("RsvpStatusIconSelector");

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
            (await ViewModel.ExternalLauncher.LaunchUriAsync(args.Uri)).ThrowIfNotSucceeded();
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
        _ = ObserveRendererOperationAsync(UpdateEditorThemeAsync(), "Calendar description theme update failed.");
    }

    void IRecipient<CalendarDescriptionRenderingRequested>.Receive(CalendarDescriptionRenderingRequested message)
    {
        _ = ObserveRendererOperationAsync(RenderDescriptionAsync(), "Calendar description rendering failed.");
    }

    /// <summary>
    /// Observes fire-and-forget renderer work. A theme change or render request can arrive after
    /// OnNavigatedFrom disposed the renderer; that is expected teardown, not a failure.
    /// </summary>
    private static async Task ObserveRendererOperationAsync(Task operation, string failureMessage)
    {
        try
        {
            await operation;
        }
        catch (ObjectDisposedException ex)
        {
            Log.Debug(ex, "Calendar description renderer was already disposed.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Calendar description renderer operation failed: {Operation}", failureMessage);
        }
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

    private void CalendarAttachmentContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: CalendarAttachmentViewModel attachment } target || ViewModel is null)
            return;

        MailCalContextFlyoutHelper.Show(target, args, (ContextFlyoutMenuEntry[])
        [
            new ContextFlyoutCommandEntry
            {
                Text = Translator.Buttons_Open,
                Icon = new ContextFlyoutIcon(MailCalIconGlyphs.GetGlyph(MailCalIconGlyph.Open)),
                Command = ViewModel.OpenAttachmentCommand,
                CommandParameter = attachment,
                AutomationId = "EventAttachmentOpen"
            },
            new ContextFlyoutCommandEntry
            {
                Text = Translator.Buttons_Save,
                Icon = new ContextFlyoutIcon(MailCalIconGlyphs.GetGlyph(MailCalIconGlyph.Save)),
                Command = ViewModel.SaveAttachmentCommand,
                CommandParameter = attachment,
                Shortcut = new ContextFlyoutShortcut("Ctrl+S", "S", Control: true),
                AutomationId = "EventAttachmentSave"
            }
        ], FlyoutPlacementMode.Right);
    }
}
