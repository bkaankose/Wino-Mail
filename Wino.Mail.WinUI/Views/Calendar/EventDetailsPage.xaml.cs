using System;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using Windows.System;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Extensions;
using Wino.Mail.WinUI.Views.Abstract;
using Wino.Messaging.Client.Calendar;
using Wino.Messaging.Client.Shell;

namespace Wino.Calendar.Views;

public sealed partial class EventDetailsPage : EventDetailsPageAbstract,
    IRecipient<ApplicationThemeChanged>,
    IRecipient<CalendarDescriptionRenderingRequested>
{
    private readonly IPreferencesService _preferencesService = App.Current.Services.GetService<IPreferencesService>()!;
    private TaskCompletionSource<bool> DOMLoadedTask = new TaskCompletionSource<bool>();
    private bool isChromiumDisposed = false;

    public EventDetailsPage()
    {
        InitializeComponent();

        Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "00FFFFFF");
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--enable-features=OverlayScrollbar,msOverlayScrollbarWinStyle,msOverlayScrollbarWinStyleAnimation,msWebView2CodeCache");
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        EventDetailsWebView.CoreWebView2Initialized -= CoreWebViewInitialized;
        EventDetailsWebView.CoreWebView2Initialized += CoreWebViewInitialized;

        _ = EventDetailsWebView.EnsureCoreWebView2Async();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        DisposeWebView2();
    }

    private async void CoreWebViewInitialized(Microsoft.UI.Xaml.Controls.WebView2 sender, CoreWebView2InitializedEventArgs args)
    {
        if (EventDetailsWebView.CoreWebView2 == null) return;

        var editorBundlePath = (await ViewModel.NativeAppService.GetEditorBundlePathAsync()).Replace("editor.html", string.Empty);

        EventDetailsWebView.CoreWebView2.SetVirtualHostNameToFolderMapping("wino.mail", editorBundlePath, CoreWebView2HostResourceAccessKind.Allow);

        EventDetailsWebView.CoreWebView2.DOMContentLoaded -= DOMContentLoaded;
        EventDetailsWebView.CoreWebView2.DOMContentLoaded += DOMContentLoaded;

        EventDetailsWebView.CoreWebView2.NewWindowRequested -= WindowRequested;
        EventDetailsWebView.CoreWebView2.NewWindowRequested += WindowRequested;

        EventDetailsWebView.NavigationStarting -= WebViewNavigationStarting;
        EventDetailsWebView.NavigationStarting += WebViewNavigationStarting;

        EventDetailsWebView.Source = new Uri("https://wino.mail/reader.html");
    }

    private void DOMContentLoaded(CoreWebView2 sender, CoreWebView2DOMContentLoadedEventArgs args)
    {
        DOMLoadedTask.TrySetResult(true);
        _ = RenderDescriptionAsync();
    }

    private async Task RenderDescriptionAsync()
    {
        if (ViewModel?.CurrentEvent?.CalendarItem == null)
            return;

        await DOMLoadedTask.Task;

        await UpdateEditorThemeAsync();
        await UpdateReaderFontPropertiesAsync();

        var description = ViewModel.CurrentEvent.CalendarItem.Description ?? string.Empty;

        if (string.IsNullOrEmpty(description))
        {
            await EventDetailsWebView.ExecuteScriptFunctionAsync("RenderHTML", isChromiumDisposed, JsonSerializer.Serialize(" ", BasicTypesJsonContext.Default.String));
        }
        else
        {
            await EventDetailsWebView.ExecuteScriptFunctionAsync("RenderHTML", isChromiumDisposed,
                JsonSerializer.Serialize(description, BasicTypesJsonContext.Default.String),
                JsonSerializer.Serialize(true, BasicTypesJsonContext.Default.Boolean));
        }
    }

    private async void WindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;

        try
        {
            await Launcher.LaunchUriAsync(new Uri(args.Uri));
        }
        catch (Exception) { }
    }

    private async void WebViewNavigationStarting(Microsoft.UI.Xaml.Controls.WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (args.Uri == "https://wino.mail/reader.html")
            return;

        args.Cancel = !args.Uri.StartsWith("data:text/html");

        if (args.Cancel && Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? newUri) && newUri != null)
        {
            await Launcher.LaunchUriAsync(newUri);
        }
    }

    private void DisposeWebView2()
    {
        if (EventDetailsWebView == null) return;

        EventDetailsWebView.CoreWebView2Initialized -= CoreWebViewInitialized;
        EventDetailsWebView.NavigationStarting -= WebViewNavigationStarting;

        if (EventDetailsWebView.CoreWebView2 != null)
        {
            EventDetailsWebView.CoreWebView2.DOMContentLoaded -= DOMContentLoaded;
            EventDetailsWebView.CoreWebView2.NewWindowRequested -= WindowRequested;
        }

        isChromiumDisposed = true;

        EventDetailsWebView.Close();
    }

    private async Task UpdateEditorThemeAsync()
    {
        await DOMLoadedTask.Task;

        if (ViewModel.IsDarkWebviewRenderer)
        {
            EventDetailsWebView.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
            await InvokeScriptSafeAsync("SetDarkEditor();");
        }
        else
        {
            EventDetailsWebView.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Light;
            await InvokeScriptSafeAsync("SetLightEditor();");
        }
    }

    private async Task UpdateReaderFontPropertiesAsync()
    {
        await EventDetailsWebView.ExecuteScriptFunctionAsync("ChangeFontSize", isChromiumDisposed, JsonSerializer.Serialize(_preferencesService.ReaderFontSize, BasicTypesJsonContext.Default.Int32));

        var fontName = _preferencesService.ReaderFont;
        fontName += ", sans-serif";

        await EventDetailsWebView.ExecuteScriptFunctionAsync("ChangeFontFamily", isChromiumDisposed, JsonSerializer.Serialize(fontName, BasicTypesJsonContext.Default.String));
    }

    private async Task<string> InvokeScriptSafeAsync(string function)
    {
        try
        {
            return await EventDetailsWebView.ExecuteScriptAsync(function);
        }
        catch (Exception) { }

        return string.Empty;
    }

    void IRecipient<ApplicationThemeChanged>.Receive(ApplicationThemeChanged message)
    {
        ViewModel.IsDarkWebviewRenderer = message.IsUnderlyingThemeDark;
        _ = UpdateEditorThemeAsync();
    }

    async void IRecipient<CalendarDescriptionRenderingRequested>.Receive(CalendarDescriptionRenderingRequested message)
    {
        await RenderDescriptionAsync();
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
