using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Editor;
using Wino.Editor.WebKitGtk;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Collections;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The reading pane: one <see cref="MailRenderingPageViewModel"/> and one WebKitGTK reader session, reused for every
/// message as on the Mac. The ViewModel pushes the prepared HTML through its render delegates.
/// </summary>
internal sealed class MailReaderView
{
    private readonly IServiceProvider _services;
    private readonly IDispatcher _dispatcher;
    private readonly Gtk.Stack _stack = Gtk.Stack.New();
    private readonly Gtk.Label _subject = Label("title-2", wrap: true);
    private readonly Gtk.Label _sender = Label("heading", wrap: false);
    private readonly Gtk.Label _recipients = Label("dim-label", wrap: false);
    private readonly Gtk.Label _date = Label("dim-label", wrap: false);
    private readonly Gtk.FlowBox _attachments = Gtk.FlowBox.New();
    private readonly Adw.Spinner _loading = Adw.Spinner.New();
    private readonly Adw.Avatar _avatar = Adw.Avatar.New(40, string.Empty, true);
    private readonly Adw.Bin _webHost = Adw.Bin.New();
    private MailRenderingPageViewModel? _viewModel;
    private WebKitGtkHtmlMailReaderSession? _reader;
    private bool _dark;
    private bool _released;

    public MailReaderView(IServiceProvider services, IDispatcher dispatcher)
    {
        _services = services;
        _dispatcher = dispatcher;

        var idle = Adw.StatusPage.New();
        idle.SetIconName("mail-read-symbolic");
        idle.SetTitle(Translator.NoMailSelected);

        var names = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        names.SetHexpand(true);
        names.Append(_sender);
        names.Append(_recipients);
        _date.SetValign(Gtk.Align.Start);
        _date.AddCssClass("numeric");
        var people = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        people.Append(_avatar);
        people.Append(names);
        people.Append(_date);

        _attachments.SetSelectionMode(Gtk.SelectionMode.None);
        _attachments.SetMaxChildrenPerLine(8);
        _attachments.SetVisible(false);

        var header = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
        header.AddCssClass("wino-reader-header");
        header.Append(_subject);
        header.Append(people);
        header.Append(_attachments);

        _loading.SetHalign(Gtk.Align.Center);
        _loading.SetValign(Gtk.Align.Center);
        _loading.SetSizeRequest(32, 32);
        _loading.SetVisible(false);
        _webHost.SetVexpand(true);
        var body = Gtk.Overlay.New();
        body.SetChild(_webHost);
        body.AddOverlay(_loading);

        var message = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        message.Append(header);
        message.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
        message.Append(body);

        _stack.AddNamed(idle, "idle");
        _stack.AddNamed(message, "message");

        var view = Adw.ToolbarView.New();
        view.AddTopBar(Adw.HeaderBar.New());
        view.SetContent(_stack);
        Root = view;
    }

    public Gtk.Widget Root { get; }

    public void ShowIdle()
    {
        if (!_released) _stack.SetVisibleChildName("idle");
    }

    public async Task ShowAsync(object parameter)
    {
        if (_released) return;
        _stack.SetVisibleChildName("message");
        if (_viewModel is { } existing)
        {
            await existing.InitializeNavigationAsync(NavigationMode.New, parameter);
            return;
        }

        var viewModel = _viewModel = _services.GetRequiredService<MailRenderingPageViewModel>();
        viewModel.Dispatcher = _dispatcher;
        var reader = _reader = new WebKitGtkHtmlMailReaderSession(_dispatcher);
        reader.Configure(viewModel.ExternalLauncher);
        reader.OperationFailed += (_, exception) => Serilog.Log.Error(exception, "The reader could not complete an operation.");
        // Links go through the ViewModel: tracking parameters are stripped and the link is confirmed when asked.
        reader.LinkOpener = (uri, text) => viewModel.OpenLinkAsync(uri, text);
        reader.View.SetVexpand(true);
        _webHost.SetChild(reader.View);

        var style = Adw.StyleManager.GetDefault();
        _dark = style.Dark;
        style.OnNotify += StyleChanged;
        viewModel.IsDarkWebviewRenderer = _dark;
        viewModel.RenderHtmlAsyncFunc = RenderAsync;
        viewModel.ClearRenderedHtmlAsyncFunc = () => _reader is { } session && !_released ? session.ClearAsync() : Task.CompletedTask;
        viewModel.PropertyChanged += ViewModelChanged;
        viewModel.ToItems.CollectionChanged += RecipientsChanged;
        viewModel.CcItems.CollectionChanged += RecipientsChanged;
        viewModel.Attachments.CollectionChanged += AttachmentsChanged;
        UpdateHeader();
        UpdateAttachments();

        await reader.InitializeAsync();
        await reader.SetThemeAsync(_dark);
        await viewModel.InitializeNavigationAsync(NavigationMode.New, parameter);
    }

    public async Task ReleaseAsync()
    {
        if (_released) return;
        _released = true;
        Adw.StyleManager.GetDefault().OnNotify -= StyleChanged;
        if (_viewModel is { } viewModel)
        {
            viewModel.PropertyChanged -= ViewModelChanged;
            viewModel.ToItems.CollectionChanged -= RecipientsChanged;
            viewModel.CcItems.CollectionChanged -= RecipientsChanged;
            viewModel.Attachments.CollectionChanged -= AttachmentsChanged;
            viewModel.OnNavigatedFrom(NavigationMode.Back, null);
        }
        if (_reader is { } reader) await reader.DisposeAsync();
    }

    private async Task RenderAsync(string html)
    {
        if (_reader is not { } reader || _viewModel is not { } viewModel || _released) return;
        var preferences = viewModel.PreferencesService;
        await reader.SetThemeAsync(_dark);
        await reader.SetReaderTypographyAsync($"{preferences.ReaderFont}, sans-serif", preferences.ReaderFontSize);
        var options = viewModel.CurrentRenderModel?.MailRenderingOptions;
        var policy = options?.RemoteContent == RemoteContentMode.BlockAll ? RemoteContentPolicy.Blocked : RemoteContentPolicy.ImagesAndFontsAllowed;
        await reader.RenderAsync(new HtmlMailReaderRequest(string.IsNullOrEmpty(html) ? " " : html, policy,
            viewModel.IsReaderViewEnabled ? HtmlMailRenderMode.Readability : HtmlMailRenderMode.Original, options?.RenderPlaintextLinks ?? true));
        await reader.SetAccessibilityContextAsync(new ReaderAccessibilityContext(
            string.IsNullOrWhiteSpace(viewModel.Subject) ? Translator.MailItemNoSubject : viewModel.Subject,
            string.IsNullOrWhiteSpace(viewModel.FromName) ? viewModel.FromAddress : $"{viewModel.FromName} <{viewModel.FromAddress}>",
            MailRowPresentation.FormatReaderDate(viewModel.CreationDate, preferences.MailTimeFormatPreference),
            Translator.Reader_MessageBodyAutomationName,
            Translator.Reader_PlainTextFallbackAutomationName,
            viewModel.CurrentRenderModel?.AccessibleText ?? string.Empty));
    }

    private void StyleChanged(GObject.Object sender, GObject.Object.NotifySignalArgs args)
    {
        var dark = Adw.StyleManager.GetDefault().Dark;
        if (dark == _dark || _released || _reader is not { } reader || _viewModel is not { } viewModel) return;
        _dark = dark;
        viewModel.IsDarkWebviewRenderer = dark;
        _ = reader.SetThemeAsync(dark);
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs args)
        => _ = _dispatcher.ExecuteOnUIThread(() =>
        {
            if (_released) return;
            if (args.PropertyName == nameof(MailRenderingPageViewModel.CurrentRenderModel)) UpdateAttachments();
            UpdateHeader();
        });

    private void RecipientsChanged(object? sender, NotifyCollectionChangedEventArgs args)
        => _ = _dispatcher.ExecuteOnUIThread(() => { if (!_released) UpdateHeader(); });

    private void AttachmentsChanged(object? sender, NotifyCollectionChangedEventArgs args)
        => _ = _dispatcher.ExecuteOnUIThread(() => { if (!_released) UpdateAttachments(); });

    private void UpdateHeader()
    {
        if (_viewModel is not { } viewModel) return;
        _subject.SetText(string.IsNullOrWhiteSpace(viewModel.Subject) ? Translator.MailItemNoSubject : viewModel.Subject);
        var name = string.IsNullOrWhiteSpace(viewModel.FromName) ? viewModel.FromAddress ?? string.Empty : viewModel.FromName;
        _avatar.SetText(name);
        _sender.SetText(string.IsNullOrWhiteSpace(viewModel.FromAddress) || name == viewModel.FromAddress ? name : $"{name}  <{viewModel.FromAddress}>");
        var recipients = viewModel.ToItems.Concat(viewModel.CcItems).Select(static contact => contact.DisplayName).Where(static text => !string.IsNullOrWhiteSpace(text)).ToArray();
        _recipients.SetText(string.Join(", ", recipients));
        _recipients.SetTooltipText(string.Join("\n", recipients));
        _date.SetText(MailRowPresentation.FormatReaderDate(viewModel.CreationDate, viewModel.PreferencesService.MailTimeFormatPreference));
        _loading.SetVisible(!viewModel.IsMailContentReady);
    }

    private void UpdateAttachments()
    {
        if (_viewModel is not { } viewModel) return;
        _attachments.RemoveAll();
        foreach (var attachment in viewModel.Attachments)
        {
            var chip = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
            chip.AddCssClass("wino-attachment");
            chip.Append(Gtk.Image.NewFromIconName("mail-attachment-symbolic"));
            chip.Append(Gtk.Label.New($"{attachment.FileName}  ({attachment.ReadableSize})"));
            _attachments.Append(chip);
        }
        _attachments.SetVisible(viewModel.Attachments.Count > 0);
    }

    private static Gtk.Label Label(string cssClass, bool wrap)
    {
        var label = Gtk.Label.New(string.Empty);
        label.SetXalign(0);
        label.SetSelectable(true);
        label.AddCssClass(cssClass);
        if (wrap) label.SetWrap(true);
        else label.SetEllipsize(Pango.EllipsizeMode.End);
        return label;
    }
}
