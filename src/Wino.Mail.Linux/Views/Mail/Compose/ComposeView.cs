using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Client.Mails;
using Wino.Core.Domain.Models.Navigation;
using Wino.Editor.WebKitGtk;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail.Compose;

/// <summary>
/// The composer in the reading pane (Mac <c>ComposePageViewController</c>): draft-upload failure bar, command row,
/// From, To/Cc/Bcc, Subject with importance and read receipt, the HTML editor and the attachment slot. Later packages
/// hook in through the partial-method pairs below and the slots; each pair has one implementation.
/// </summary>
internal sealed partial class ComposeView : WinoView<ComposePageViewModel>
{
    private readonly IExternalLauncher _launcher;
    private readonly IKeyboardShortcutService _shortcuts;
    private readonly LinuxSenderPictureProvider _pictures;
    private readonly ITranslationService _translations;
    private readonly ISettingsWindowPresenter _settings;
    private readonly IServiceProvider _provider;
    private readonly Gtk.Overlay _overlay = Gtk.Overlay.New();
    private readonly Gtk.Box _page = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private readonly Adw.Bin _editorHost = Adw.Bin.New();
    private readonly WinoInfoBar _syncFailedBar = new(WinoInfoBarSeverity.Warning, Translator.Draft_SyncFailedInfoBarTitle);
    private readonly Gtk.Button _sendButton;
    private readonly Gtk.Button _sendToServerButton = Gtk.Button.NewWithLabel(Translator.Buttons_SendToServer);
    private readonly Gtk.Button _discardButton = WinoIcons.Button(WinoIconGlyph.Delete, Translator.Buttons_Discard);
    private readonly Adw.Spinner _draftSpinner = Adw.Spinner.New();
    private readonly Gtk.Label _draftStatus = Gtk.Label.New(string.Empty);
    private bool _editorDisposed;

    public ComposeView(ComposePageViewModel viewModel, WinoViewServices services, IExternalLauncher launcher, IKeyboardShortcutService shortcuts,
        LinuxSenderPictureProvider pictures, ITranslationService translations, ISettingsWindowPresenter settings,
        IServiceProvider provider) : base(viewModel, services)
    {
        _provider = provider;
        _settings = settings;
        _translations = translations;
        _launcher = launcher;
        _shortcuts = shortcuts;
        _pictures = pictures;

        _syncFailedBar.IsClosable = false;
        _syncFailedBar.ActionTitle = Translator.Draft_RetryUpload;
        _syncFailedBar.IsOpen = false;
        _syncFailedBar.Root.SetMarginTop(10);
        _syncFailedBar.Root.SetMarginStart(12);
        _syncFailedBar.Root.SetMarginEnd(12);

        _sendButton = Gtk.Button.New();
        _sendButton.SetChild(WinoIcons.ButtonContent(WinoIconGlyph.Send, Translator.Buttons_Send, 14));
        _sendButton.AddCssClass("suggested-action");
        WinoA11y.Rename(_sendButton, Translator.Buttons_Send);
        _sendToServerButton.SetTooltipText(Translator.Composer_LocalDraftSyncInfo);
        _sendToServerButton.SetVisible(false);
        WinoA11y.Description(_sendToServerButton, Translator.Composer_LocalDraftSyncInfo);
        _discardButton.SetTooltipText(Translator.Buttons_Discard);

        _draftSpinner.SetSizeRequest(14, 14);
        _draftSpinner.SetVisible(false);
        _draftStatus.AddCssClass("dim-label");
        _draftStatus.AddCssClass("caption");
        _draftStatus.SetEllipsize(Pango.EllipsizeMode.End);
        _draftStatus.SetHexpand(true);
        _draftStatus.SetXalign(1);

        CommandSlot.SetSpacing(4);
        var commandRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        commandRow.AddCssClass("wino-compose-commands");
        commandRow.SetMarginTop(10);
        commandRow.SetMarginBottom(10);
        commandRow.SetMarginStart(16);
        commandRow.SetMarginEnd(12);
        commandRow.Append(_sendButton);
        commandRow.Append(_sendToServerButton);
        commandRow.Append(CommandSlot);
        commandRow.Append(_draftStatus);
        commandRow.Append(_draftSpinner);
        commandRow.Append(PopOutSlot);
        commandRow.Append(_discardButton);
        WinoA11y.Label(commandRow, Translator.Composer_CommandBarLabel);

        var editorFrame = Gtk.Frame.New(null);
        editorFrame.AddCssClass("view");
        editorFrame.AddCssClass("wino-compose-editor");
        editorFrame.SetChild(_editorHost);
        editorFrame.SetVexpand(true);
        editorFrame.SetMarginTop(6);
        editorFrame.SetMarginStart(12);
        editorFrame.SetMarginEnd(12);
        editorFrame.SetMarginBottom(12);
        _editorHost.SetVexpand(true);
        WinoA11y.Label(_editorHost, Translator.Reader_MessageBodyAutomationName);

        _page.Append(_syncFailedBar.Root);
        _page.Append(commandRow);
        _page.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
        _page.Append(BuildFields());
        _page.Append(ToolbarSlot);
        _page.Append(editorFrame);
        _page.Append(AttachmentSlot);
        _overlay.SetChild(_page);
        _overlay.AddCssClass("wino-compose");
    }

    public override Gtk.Widget Root => _overlay;
    internal WebKitGtkHtmlMailEditorSession? Editor { get; private set; }
    internal Gtk.Box CommandSlot { get; } = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
    internal Gtk.Box PopOutSlot { get; } = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
    internal Gtk.Box ToolbarSlot { get; } = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    internal Gtk.Box AttachmentSlot { get; } = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    internal Gtk.Overlay PageOverlay => _overlay;
    internal Gtk.Widget EditorHost => _editorHost;
    internal bool IsPoppedOut { get; set; }

    partial void InitializeRecipients();
    partial void DeactivateRecipients();
    partial void InitializeSuggestions();
    partial void DeactivateSuggestions();
    partial void InitializeToolbar();
    partial void DeactivateToolbar();
    partial void InitializeInsert();
    partial void DeactivateInsert();
    partial void InitializeAttachments();
    partial void DeactivateAttachments();
    partial void InitializeFind();
    partial void DeactivateFind();
    partial void InitializeSignatures();
    partial void DeactivateSignatures();
    partial void InitializePopOut();
    partial void DeactivatePopOut();
    partial void InitializeRewrite();
    partial void DeactivateRewrite();
    partial void InitializeSecurity();
    partial void DeactivateSecurity();
    partial void ClosePopOutWindow();

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        var editor = Editor = new WebKitGtkHtmlMailEditorSession(Dispatcher);
        editor.Configure(_launcher);
        editor.OperationFailed += (_, exception) => ReportError(exception);
        editor.ContentChanged += (_, _) => EditorContentChanged();
        editor.Restarted += EditorRestarted;
        editor.ApplicationShortcutRequested += EditorApplicationShortcutRequested;
        editor.View.SetVexpand(true);
        editor.View.SetHexpand(true);
        editor.View.SetBackgroundColor(new Gdk.RGBA { Red = 0, Green = 0, Blue = 0, Alpha = 0 });
        _editorHost.SetChild(editor.View);
        var focusOnOpen = ConsumeInitialFocusRequest(parameter as Wino.Mail.ViewModels.Data.MailItemViewModel);

        ViewModel.GetHTMLBodyFunction = GetHtmlBodyAsync;
        ViewModel.RenderHtmlBodyAsyncFunc = RenderBodyAsync;
        ViewModel.BeforeAutosave = BeforeAutosaveAsync;
        ViewModel.CloseRequested += CloseRequested;
#if DEBUG
        Infrastructure.Diagnostics.ComposeDebugActions.SetCurrent(this);
#endif

        BindPage();
        InitializeRecipients();
        InitializeSuggestions();
        InitializeToolbar();
        InitializeInsert();
        InitializeAttachments();
        InitializeFind();
        InitializeSignatures();
        InitializePopOut();
        InitializeRewrite();
        InitializeSecurity();

        await editor.SetLinkLabelsAsync(Translator.Composer_OpenLink, Translator.Composer_RemoveLink);
        await editor.InitializeAsync();
        if (IsReleased) return;
        await editor.SetThemeAsync(_dark);
        await ApplySendShortcutAsync();
        if (IsReleased) return;
        await ViewModel.InitializeNavigationAsync(mode, parameter);
        if (IsReleased) return;
        await ApplyInitialFocusAsync(focusOnOpen);
    }

    protected override async Task DeactivateAsync()
    {
        DeactivateSecurity();
        DeactivateRewrite();
        DeactivatePopOut();
        DeactivateSignatures();
        DeactivateFind();
        DeactivateAttachments();
        DeactivateInsert();
        DeactivateToolbar();
        DeactivateSuggestions();
        DeactivateRecipients();
        ViewModel.CancelAutosave();
        ViewModel.CloseRequested -= CloseRequested;
        ViewModel.RenderHtmlBodyAsyncFunc = null;
        ViewModel.BeforeAutosave = null;
        try
        {
            await SyncAllRecipientsAsync();
            await ViewModel.UpdateMimeChangesAsync();
        }
        catch (Exception exception) { ReportError(exception); }
        finally
        {
            ViewModel.GetHTMLBodyFunction = null;
            await DisposeEditorAsync();
            ViewModel.OnNavigatedFrom(NavigationMode.New, null);
        }
    }

    private async Task DisposeEditorAsync()
    {
        if (Editor is not { } editor || _editorDisposed) return;
        _editorDisposed = true;
        try { await editor.DisposeAsync(); }
        catch (Exception exception) { ReportError(exception); }
        await Dispatcher.ExecuteOnUIThread(() => _editorHost.SetChild(null));
        Editor = null;
    }

    private void BindPage()
    {
        Bind.Bind(nameof(ComposePageViewModel.CurrentMailDraftItem), static vm => vm.CurrentMailDraftItem, _ => UpdateSendButtons());
        Bind.Bind(nameof(ComposePageViewModel.ShouldShowSendButton), static vm => vm.ShouldShowSendButton, _ => UpdateSendButtons());
        Bind.Bind(nameof(ComposePageViewModel.ShouldShowSendToServerButton), static vm => vm.ShouldShowSendToServerButton, _ => UpdateSendButtons());
        Bind.Bind(nameof(ComposePageViewModel.IsDraftBusy), static vm => vm.IsDraftBusy, _ => { UpdateSendButtons(); UpdateDraftStatus(); });
        Bind.Bind(nameof(ComposePageViewModel.LastSavedText), static vm => vm.LastSavedText, _ => UpdateDraftStatus());
        Bind.Bind(nameof(ComposePageViewModel.IsDraftSyncFailed), static vm => vm.IsDraftSyncFailed, _ => UpdateSyncFailedBar());
        Bind.Bind(nameof(ComposePageViewModel.DraftSyncErrorMessage), static vm => vm.DraftSyncErrorMessage, _ => UpdateSyncFailedBar());
        Bind.BindCommand(_sendToServerButton, ViewModel.SendToServerCommand);
        Bind.BindCommand(_discardButton, ViewModel.DiscardCommand);
        Bindings.Own(new Wino.Presentation.Bindings.CommandBinding(ViewModel.SendCommand, static () => null, _sendButton.SetSensitive, Dispatcher, ReportError));
        Bindings.Own(GtkSignals.OnClicked(_sendButton, () => Observe(SendAsync())));
        Action<WinoInfoBar> retry = _ =>
        {
            if (ViewModel.SendToServerCommand.CanExecute(null)) Observe(ViewModel.SendToServerCommand.ExecuteAsync(null));
        };
        _syncFailedBar.ActionInvoked += retry;
        Bindings.Own(new ActionDisposable(() => _syncFailedBar.ActionInvoked -= retry));
        Bindings.Own(_syncFailedBar);
        BindFields();
        BindEditorTheme();
    }

    private void UpdateSendButtons()
    {
        _sendButton.SetVisible(ViewModel.ShouldShowSendButton);
        _sendToServerButton.SetVisible(ViewModel.ShouldShowSendToServerButton);
    }

    private void UpdateDraftStatus()
    {
        var busy = ViewModel.IsDraftBusy;
        _draftSpinner.SetVisible(busy);
        _draftStatus.SetText(busy ? Translator.Composer_SavingDraft : ViewModel.LastSavedText ?? string.Empty);
    }

    private void UpdateSyncFailedBar()
    {
        _syncFailedBar.Message = ViewModel.DraftSyncErrorMessage;
        _syncFailedBar.IsOpen = ViewModel.IsDraftSyncFailed;
    }

    private void ScheduleAutosave()
    {
        if (IsReleased) return;
        Observe(ViewModel.ScheduleAutosave());
    }

    private async Task<bool> BeforeAutosaveAsync()
    {
        if (_editorDisposed || IsReleased) return false;
        await SyncAllRecipientsAsync(includeTyped: false);
        return true;
    }

    internal async Task SendAsync()
    {
        await SyncAllRecipientsAsync();
        if (ViewModel.SendCommand.CanExecute(null)) await ViewModel.SendCommand.ExecuteAsync(null);
    }

    private void CloseRequested(object? sender, EventArgs args) => RunOnUI(() =>
    {
        if (IsPoppedOut)
        {
            ClosePopOutWindow();
            return;
        }
        Messenger.Send(new DisposeRenderingFrameRequested());
    });

    private static bool ConsumeInitialFocusRequest(Wino.Mail.ViewModels.Data.MailItemViewModel? draft)
    {
        if (draft is not { ShouldFocusComposerOnOpen: true }) return false;
        draft.ShouldFocusComposerOnOpen = false;
        return true;
    }
}
