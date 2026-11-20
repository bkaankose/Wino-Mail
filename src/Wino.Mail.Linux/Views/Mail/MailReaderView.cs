using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Editor;
using Wino.Editor.WebKitGtk;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Collections;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The reading pane: one <see cref="MailRenderingPageViewModel"/> and one WebKitGTK reader session, reused for every
/// message as on the Mac. The ViewModel pushes the prepared HTML through its render delegates.
/// </summary>
internal sealed class MailReaderView : WinoView<MailRenderingPageViewModel>, IRenavigableView
{
    private readonly record struct HeaderState(string Subject, string Avatar, string Sender, string Date, bool Loading);

    private readonly Gtk.Stack _stack = Gtk.Stack.New();
    private readonly Gtk.Label _subject = Label("title-2", wrap: true);
    private readonly Gtk.Label _sender = Label("heading", wrap: false);
    private readonly Gtk.Label _recipients = Label("dim-label", wrap: false);
    private readonly Gtk.Label _date = Label("dim-label", wrap: false);
    private readonly Gtk.FlowBox _attachments = Gtk.FlowBox.New();
    private readonly Adw.Spinner _loading = Adw.Spinner.New();
    private readonly Adw.Avatar _avatar = Adw.Avatar.New(40, string.Empty, true);
    private readonly Adw.Bin _webHost = Adw.Bin.New();
    private WebKitGtkHtmlMailReaderSession? _reader;
    private bool _dark;

    public MailReaderView(MailRenderingPageViewModel viewModel, WinoViewServices services) : base(viewModel, services)
    {
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

    public override Gtk.Widget Root { get; }

    public void ShowIdle()
    {
        if (!IsReleased) _stack.SetVisibleChildName("idle");
    }

    public Task ShowAsync(object parameter)
    {
        if (IsReleased) return Task.CompletedTask;
        _stack.SetVisibleChildName("message");
        return IsActive ? RenavigateAsync(parameter) : ActivateAsync(NavigationMode.New, parameter);
    }

    public Task RenavigateAsync(object? parameter)
        => IsReleased ? Task.CompletedTask : ViewModel.InitializeNavigationAsync(NavigationMode.New, parameter);

    // OnNavigatedTo is not called: on this ViewModel it wraps InitializeNavigationAsync and would navigate a second time.
    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        var viewModel = ViewModel;
        var reader = _reader = new WebKitGtkHtmlMailReaderSession(Dispatcher);
        reader.Configure(viewModel.ExternalLauncher);
        reader.OperationFailed += (_, exception) => ReportError(exception);
        // Links go through the ViewModel: tracking parameters are stripped and the link is confirmed when asked.
        reader.LinkOpener = (uri, text) => viewModel.OpenLinkAsync(uri, text);
        reader.View.SetVexpand(true);
        _webHost.SetChild(reader.View);

        _dark = Adw.StyleManager.GetDefault().Dark;
        Bindings.Own(GtkSignals.OnNotify(Adw.StyleManager.GetDefault(), "dark", StyleChanged));
        viewModel.IsDarkWebviewRenderer = _dark;
        viewModel.RenderHtmlAsyncFunc = RenderAsync;
        viewModel.ClearRenderedHtmlAsyncFunc = () => _reader is { } session && !IsReleased ? session.ClearAsync() : Task.CompletedTask;

        Bind.BindAny(ReadHeader, ApplyHeader);
        Bind.BindCollection(viewModel.ToItems, UpdateRecipients);
        Bind.BindCollection(viewModel.CcItems, UpdateRecipients);
        Bind.BindCollection(viewModel.Attachments, UpdateAttachments);
        Bind.Bind(nameof(MailRenderingPageViewModel.CurrentRenderModel), static vm => vm.CurrentRenderModel, _ => UpdateAttachments());

        await reader.InitializeAsync();
        if (IsReleased) return;
        await reader.SetThemeAsync(_dark);
        if (IsReleased) return;
        await viewModel.InitializeNavigationAsync(mode, parameter);
    }

    protected override async Task DeactivateAsync()
    {
        ViewModel.OnNavigatedFrom(NavigationMode.Back, null);
        if (_reader is { } reader) await reader.DisposeAsync();
    }

    private async Task RenderAsync(string html)
    {
        if (_reader is not { } reader || IsReleased) return;
        var viewModel = ViewModel;
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

    private void StyleChanged()
    {
        var dark = Adw.StyleManager.GetDefault().Dark;
        if (dark == _dark || IsReleased || _reader is not { } reader) return;
        _dark = dark;
        ViewModel.IsDarkWebviewRenderer = dark;
        Observe(reader.SetThemeAsync(dark));
    }

    private static HeaderState ReadHeader(MailRenderingPageViewModel viewModel)
    {
        var name = string.IsNullOrWhiteSpace(viewModel.FromName) ? viewModel.FromAddress ?? string.Empty : viewModel.FromName;
        return new HeaderState(
            string.IsNullOrWhiteSpace(viewModel.Subject) ? Translator.MailItemNoSubject : viewModel.Subject,
            name,
            string.IsNullOrWhiteSpace(viewModel.FromAddress) || name == viewModel.FromAddress ? name : $"{name}  <{viewModel.FromAddress}>",
            MailRowPresentation.FormatReaderDate(viewModel.CreationDate, viewModel.PreferencesService.MailTimeFormatPreference),
            !viewModel.IsMailContentReady);
    }

    private void ApplyHeader(HeaderState header)
    {
        _subject.SetText(header.Subject);
        _avatar.SetText(header.Avatar);
        _sender.SetText(header.Sender);
        _date.SetText(header.Date);
        _loading.SetVisible(header.Loading);
    }

    private void UpdateRecipients()
    {
        var recipients = ViewModel.ToItems.Concat(ViewModel.CcItems).Select(static contact => contact.DisplayName).Where(static text => !string.IsNullOrWhiteSpace(text)).ToArray();
        _recipients.SetText(string.Join(", ", recipients));
        _recipients.SetTooltipText(string.Join("\n", recipients));
    }

    private void UpdateAttachments()
    {
        _attachments.RemoveAll();
        foreach (var attachment in ViewModel.Attachments)
        {
            var chip = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
            chip.AddCssClass("wino-attachment");
            chip.Append(Gtk.Image.NewFromIconName("mail-attachment-symbolic"));
            chip.Append(Gtk.Label.New($"{attachment.FileName}  ({attachment.ReadableSize})"));
            _attachments.Append(chip);
        }
        _attachments.SetVisible(ViewModel.Attachments.Count > 0);
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
