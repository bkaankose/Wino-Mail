using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI;
using CommunityToolkit.WinUI.Controls;
using EmailValidation;
using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;
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
    private readonly List<IDisposable> _disposables = [];
    private TaskCompletionSource<bool> DOMLoadedTask = new TaskCompletionSource<bool>();

    public EventDetailsPage()
    {
        InitializeComponent();

        WebViewExtensions.EnsureWebView2Environment();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        EventDetailsWebView.CoreWebView2Initialized -= CoreWebViewInitialized;
        EventDetailsWebView.CoreWebView2Initialized += CoreWebViewInitialized;

        _disposables.Add(GetSuggestionBoxDisposable(AttendeeBox));
        _disposables.Add(EventNotesEditor);
        ViewModel.GetHtmlNotesAsync = async () => await EventNotesEditor.GetHtmlBodyAsync() ?? string.Empty;

        _ = EventDetailsWebView.EnsureCoreWebView2Async();
        _ = RenderEditorDescriptionAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _disposables.Clear();
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
        if (DispatcherQueue != null && !DispatcherQueue.HasThreadAccess)
        {
            await DispatcherQueue.EnqueueAsync(RenderDescriptionAsync);
            return;
        }

        if (ViewModel?.CurrentEvent?.CalendarItem == null)
            return;

        await DOMLoadedTask.Task;

        await UpdateEditorThemeAsync();
        await UpdateReaderFontPropertiesAsync();

        var description = ViewModel.CurrentEvent.CalendarItem.Description ?? string.Empty;

        if (string.IsNullOrEmpty(description))
        {
            await EventDetailsWebView.ExecuteScriptFunctionAsync("RenderHTML", JsonSerializer.Serialize(" ", BasicTypesJsonContext.Default.String));
        }
        else
        {
            await EventDetailsWebView.ExecuteScriptFunctionAsync("RenderHTML",
                JsonSerializer.Serialize(description, BasicTypesJsonContext.Default.String),
                JsonSerializer.Serialize(true, BasicTypesJsonContext.Default.Boolean));
        }
    }

    private async Task RenderEditorDescriptionAsync()
    {
        if (DispatcherQueue != null && !DispatcherQueue.HasThreadAccess)
        {
            await DispatcherQueue.EnqueueAsync(RenderEditorDescriptionAsync);
            return;
        }

        if (ViewModel?.CurrentEvent?.CalendarItem == null || EventNotesEditor == null)
            return;

        var description = ViewModel.CurrentEvent.CalendarItem.Description;
        await EventNotesEditor.RenderHtmlAsync(string.IsNullOrEmpty(description) ? " " : description);
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

        EventDetailsWebView.Close();
    }

    private async Task UpdateEditorThemeAsync()
    {
        if (DispatcherQueue != null && !DispatcherQueue.HasThreadAccess)
        {
            await DispatcherQueue.EnqueueAsync(UpdateEditorThemeAsync);
            return;
        }

        await DOMLoadedTask.Task;

        if (ViewModel.IsDarkWebviewRenderer)
        {
            EventDetailsWebView.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
            await EventDetailsWebView.ExecuteScriptSafeAsync("SetDarkEditor();");
        }
        else
        {
            EventDetailsWebView.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Light;
            await EventDetailsWebView.ExecuteScriptSafeAsync("SetLightEditor();");
        }
    }

    private async Task UpdateReaderFontPropertiesAsync()
    {
        await EventDetailsWebView.ExecuteScriptFunctionAsync("ChangeFontSize", JsonSerializer.Serialize(_preferencesService.ReaderFontSize, BasicTypesJsonContext.Default.Int32));

        var fontName = _preferencesService.ReaderFont;
        fontName += ", sans-serif";

        await EventDetailsWebView.ExecuteScriptFunctionAsync("ChangeFontFamily", JsonSerializer.Serialize(fontName, BasicTypesJsonContext.Default.String));
    }

    void IRecipient<ApplicationThemeChanged>.Receive(ApplicationThemeChanged message)
    {
        ViewModel.IsDarkWebviewRenderer = message.IsUnderlyingThemeDark;
        EventNotesEditor.IsEditorDarkMode = message.IsUnderlyingThemeDark;
        _ = UpdateEditorThemeAsync();
    }

    void IRecipient<CalendarDescriptionRenderingRequested>.Receive(CalendarDescriptionRenderingRequested message)
    {
        _ = RenderDescriptionAsync();
        _ = RenderEditorDescriptionAsync();
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

    private IDisposable GetSuggestionBoxDisposable(TokenizingTextBox box)
    {
        return Observable.FromEventPattern<TypedEventHandler<AutoSuggestBox, AutoSuggestBoxTextChangedEventArgs>, AutoSuggestBoxTextChangedEventArgs>(
                handler => box.TextChanged += handler,
                handler => box.TextChanged -= handler)
            .Throttle(TimeSpan.FromMilliseconds(120))
            .ObserveOn(SynchronizationContext.Current!)
            .Subscribe(async eventPattern =>
            {
                if (eventPattern.EventArgs.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
                    return;

                if (eventPattern.Sender is not AutoSuggestBox senderBox || senderBox.Text.Length < 2)
                    return;

                var addresses = await ViewModel.SearchContactsAsync(senderBox.Text).ConfigureAwait(false);
                await ViewModel.ExecuteUIThread(() => senderBox.ItemsSource = addresses);
            });
    }

    private async void TokenItemAdding(TokenizingTextBox sender, TokenItemAddingEventArgs args)
    {
        if (!EmailValidator.Validate(args.TokenText))
        {
            args.Cancel = true;
            ViewModel.NotifyInvalidEmail(args.TokenText);
            return;
        }

        var deferral = args.GetDeferral();

        try
        {
            var attendee = await ViewModel.GetAttendeeAsync(args.TokenText);
            if (attendee == null)
            {
                args.Cancel = true;
                ViewModel.NotifyAddressExists();
                return;
            }

            args.Item = attendee;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void AddressBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TokenizingTextBox tokenizingTextBox)
            return;

        if (tokenizingTextBox.Items.LastOrDefault() is not ITokenStringContainer info)
            return;

        var currentText = info.Text;
        if (string.IsNullOrWhiteSpace(currentText) || !EmailValidator.Validate(currentText))
            return;

        var attendee = await ViewModel.GetAttendeeAsync(currentText);
        if (attendee == null)
        {
            tokenizingTextBox.Text = string.Empty;
            return;
        }

        ViewModel.AddAttendee(attendee);
        tokenizingTextBox.Text = string.Empty;
    }

    private void RemoveAttendeeClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CalendarComposeAttendeeViewModel attendee })
        {
            ViewModel.RemoveAttendeeCommand.Execute(attendee);
        }
    }
}
