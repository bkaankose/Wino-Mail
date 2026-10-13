using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Editor;
using Wino.Editor.WebKit;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Views.Mail.Compose;
using Wino.Mail.ViewModels;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// The composer (Mac ComposePageViewController, design boards "Compose" and "Compose window") for iPhone and iPad.
/// It opens in the iPad reader column and as a sheet; as the root of a presented navigation sheet it shows Cancel,
/// which asks to save or discard the draft. The navigation bar carries the filled Send button (Send to server for a
/// local draft that failed to upload), Attach and More (Cc/Bcc, find, rewrite, templates, signature, importance,
/// S/MIME, read receipt, editor theme, discard). Inside one Wino zone: the draft-upload failure bar, From, To / Cc /
/// Bcc token fields with the recipient suggestion list, Subject, the Wino Intelligence rewrite strip, the attachment
/// tray, the find bar and the HTML body in a WKWebView whose keyboard accessory is the Format / Insert / Options bar.
/// Partials: Toolbar, Recipients, Attachments, Rewrite, Extras, Options, Find, Drop and Debug.
/// </summary>
public sealed partial class ComposePageViewController(ComposePageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger, IServiceProvider services)
    : WinoViewController<ComposePageViewModel>(viewModel, dispatcher, logger), IReaderFrameChild
{
    private readonly IExternalLauncher? _launcher = services.GetService<IExternalLauncher>();
    private readonly UIKitNavigationService? _navigation = services.GetService<UIKitNavigationService>();
    private readonly ITranslationService? _translations = services.GetService<ITranslationService>();
    private readonly IPictureStorageService? _pictures = services.GetService<IPictureStorageService>();
    private readonly IMailDialogService? _dialogs = services.GetService<IMailDialogService>();
    private readonly IUIKitShellHost? _shellHost = services.GetService<IUIKitShellHost>();

    private readonly ComposeEditorWebView _webView = new();
    private readonly WinoZoneView _zone = new();
    private readonly UIStackView _content = WinoLayout.VStack(0);
    private readonly WinoInfoBar _syncFailedBar = new(WinoInfoBarSeverity.Warning, Translator.Draft_SyncFailedInfoBarTitle) { IsClosable = false, ActionTitle = Translator.Draft_RetryUpload };
    private readonly UIView _syncFailedHost = new() { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
    private readonly UIButton _fromButton = new() { TranslatesAutoresizingMaskIntoConstraints = false, ShowsMenuAsPrimaryAction = true };
    private readonly UILabel _draftStatus = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
    private readonly UIActivityIndicatorView _draftSpinner = new(UIActivityIndicatorViewStyle.Medium) { HidesWhenStopped = true, TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UIButton _ccBccButton = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UITextField _subjectField = new()
    {
        Font = WinoStyle.Body,
        AdjustsFontForContentSizeCategory = true,
        ReturnKeyType = UIReturnKeyType.Next,
        AutocapitalizationType = UITextAutocapitalizationType.Sentences,
        TranslatesAutoresizingMaskIntoConstraints = false
    };
    private readonly UIButton _sendButton = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UIBarButtonItem _attachItem = new() { AccessibilityLabel = Translator.Composer_AttachFiles };
    private readonly UIBarButtonItem _moreItem = new() { AccessibilityLabel = Translator.More };
    private readonly UIBarButtonItem _popOutItem = new() { AccessibilityLabel = Translator.Buttons_PopOut };
    private IReaderFrameHost? _frameHost;
    private UIBarButtonItem? _sendItem;
    private UIBarButtonItem? _cancelItem;
    private UIBarButtonItem[]? _hostRightItems;
    private ComposeRecipientField? _toField;
    private ComposeRecipientField? _ccField;
    private ComposeRecipientField? _bccField;
    private WebKitHtmlMailEditorSession? _editor;
    private CancellationTokenSource? _autosave;
    private bool _editorDisposed;
    private bool _closing;
    private bool _sendEnabled;
    private bool _sendToServerEnabled;
    private string _lastSavedStatus = string.Empty;

    /// <summary>
    /// Replaces the default close: dismissing the sheet through the shell host when this page is a sheet root,
    /// otherwise <c>DisposeRenderingFrameRequested</c> (the Mac docked composer). The router can set it to close
    /// through its own bookkeeping. It runs after Send and after Cancel saved or discarded the draft.
    /// </summary>
    public Func<Task>? CloseHandler { get; set; }

    /// <summary>
    /// The iPad reader column while the composer is docked in it (<see cref="IReaderFrameChild"/>): the bar then offers
    /// "Open in new window" when a second window scene can open. Null on iPhone, in a sheet and in a pop-out window.
    /// </summary>
    public IReaderFrameHost? FrameHost
    {
        get => _frameHost;
        set
        {
            _frameHost = value;
            ApplyNavigationItems();
        }
    }

    /// <summary>True while this page is the root of a presented navigation controller (the compose sheet).</summary>
    public bool IsSheetRoot
        => NavigationController is { } navigation && navigation.PresentingViewController is not null
           && navigation.ViewControllers is { Length: > 0 } stack && ReferenceEquals(stack[0], this);

    private ComposeRecipientField ToField => _toField ??= new ComposeRecipientField(FieldCaption(Translator.ComposerTo), _ccBccButton);
    private ComposeRecipientField CcField => _ccField ??= new ComposeRecipientField("Cc");
    private ComposeRecipientField BccField => _bccField ??= new ComposeRecipientField("Bcc");

    private static string FieldCaption(string text) => text.Trim().TrimEnd(':');

    public override void LoadView()
    {
        var root = new UIView { BackgroundColor = UIColor.Clear };

        // ---- Draft upload failure (Windows DraftSyncFailedInfoBar) ----
        _syncFailedBar.TranslatesAutoresizingMaskIntoConstraints = false;
        _syncFailedBar.ActionInvoked += (_, _) =>
        {
            if (ViewModel.SendToServerCommand.CanExecute(null)) Observe(ViewModel.SendToServerCommand.ExecuteAsync(null));
        };
        WinoLayout.Fill(_syncFailedBar, _syncFailedHost, 10, 12, 4, 12);

        // ---- From ----
        _fromButton.Configuration = FromConfiguration(string.Empty);
        _fromButton.HorizontalAlignment = UIControlContentHorizontalAlignment.Leading;
        _fromButton.AccessibilityLabel = FieldCaption(Translator.ComposerFrom);
        _fromButton.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        _fromButton.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
        _draftStatus.SetContentCompressionResistancePriority(260, UILayoutConstraintAxis.Horizontal);
        _draftStatus.SetContentHuggingPriority(750, UILayoutConstraintAxis.Horizontal);
        var fromRow = FieldRow(FieldCaption(Translator.ComposerFrom), _fromButton, _draftSpinner, _draftStatus);

        // ---- Recipients ----
        var ccBcc = UIButtonConfiguration.PlainButtonConfiguration;
        ccBcc.Title = "Cc/Bcc";
        ccBcc.BaseForegroundColor = WinoStyle.SecondaryText;
        ccBcc.ButtonSize = UIButtonConfigurationSize.Small;
        _ccBccButton.Configuration = ccBcc;
        _ccBccButton.AccessibilityLabel = Translator.Composer_CcBcc;
        _ccBccButton.SetContentCompressionResistancePriority(1000, UILayoutConstraintAxis.Horizontal);
        _ccBccButton.SetContentHuggingPriority(1000, UILayoutConstraintAxis.Horizontal);
        _ccBccButton.PrimaryActionTriggered += (_, _) => { ViewModel.IsCCBCCVisible = true; CcField.Focus(); };
        BuildRecipientFields();

        // ---- Subject ----
        _subjectField.Placeholder = FieldCaption(Translator.ComposerSubject);
        _subjectField.AccessibilityLabel = FieldCaption(Translator.ComposerSubject);
        _subjectField.EditingChanged += (_, _) =>
        {
            // The subject follows committed text only: an input method's marked text is still being composed.
            if (_subjectField.MarkedTextRange is not null) return;
            ViewModel.Subject = _subjectField.Text ?? string.Empty;
            ScheduleAutosave();
        };
        // Return in the subject moves into the message body (Windows SubjectTextBoxPreviewKeyDown).
        _subjectField.ShouldReturn = _ => { FocusBody(); return false; };
        _subjectField.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        var subjectRow = FieldRow(FieldCaption(Translator.ComposerSubject), _subjectField);

        // ---- Body ----
        _webView.AccessibilityLabel = Translator.Reader_MessageBodyAutomationName;
        _webView.SetContentHuggingPriority(1, UILayoutConstraintAxis.Vertical);
        _webView.SetContentCompressionResistancePriority(1, UILayoutConstraintAxis.Vertical);
        var bodyHost = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(_webView, bodyHost, 4, 10, 0, 10);
        bodyHost.SetContentHuggingPriority(1, UILayoutConstraintAxis.Vertical);
        bodyHost.SetContentCompressionResistancePriority(1, UILayoutConstraintAxis.Vertical);

        _content.Alignment = UIStackViewAlignment.Fill;
        foreach (var view in new UIView[] { _syncFailedHost, fromRow, ToField, CcField, BccField, subjectRow, BuildRewriteStrip(), BuildAttachmentTray(), BuildFindBar(), bodyHost })
            _content.AddArrangedSubview(view);
        WinoLayout.Fill(_content, _zone.ContentView);
        BuildSuggestionList(_zone.ContentView);

        root.AddSubview(_zone);
        var guide = root.SafeAreaLayoutGuide;
        var gutter = (nfloat)WinoStyle.ZoneGutter;
        NSLayoutConstraint.ActivateConstraints(
        [
            _zone.TopAnchor.ConstraintEqualTo(guide.TopAnchor, gutter),
            _zone.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, gutter),
            _zone.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -gutter),
            // The keyboard guide includes the formatting bar, so the body ends above it while editing.
            _zone.BottomAnchor.ConstraintEqualTo(root.KeyboardLayoutGuide.TopAnchor, -gutter)
        ]);
        View = root;

        BuildEditorToolbar();
        BuildNavigationItems();
        SetUpFileDrop();
        WinoTraits.OnAppearanceChanged(root, _ => FollowAppearance());
    }

    // ---- View helpers ----

    /// <summary>A header row: the caption, its content and a hairline below (Mac FieldRow).</summary>
    private static UIView FieldRow(string caption, params UIView[] content)
    {
        var label = WinoStyle.Label(caption, WinoStyle.Body, WinoStyle.SecondaryText);
        label.SetContentHuggingPriority(1000, UILayoutConstraintAxis.Horizontal);
        label.SetContentCompressionResistancePriority(1000, UILayoutConstraintAxis.Horizontal);
        label.IsAccessibilityElement = false;
        var views = new List<UIView> { label };
        views.AddRange(content);
        var row = WinoLayout.HStack(8, views.ToArray());
        WinoLayout.Insets(row, 6, 16, 6, 12);
        row.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        var host = WinoLayout.VStack(0, row, new WinoSeparator());
        host.Alignment = UIStackViewAlignment.Fill;
        return host;
    }

    private static UIButtonConfiguration FromConfiguration(string title)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Title = title;
        configuration.BaseForegroundColor = WinoStyle.PrimaryText;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(4, 0, 4, 0);
        configuration.TitleLineBreakMode = UILineBreakMode.TailTruncation;
        return configuration;
    }

    private UIViewController Presenter => PresentedViewController is { IsBeingDismissed: false } presented ? presented : this;

    /// <summary>Presents <paramref name="controller"/> as a popover from More; iPhone adapts it to a sheet.</summary>
    private void PresentFromMore(UIViewController controller)
    {
        var navigation = new UINavigationController(controller) { ModalPresentationStyle = UIModalPresentationStyle.Popover };
        if (navigation.PopoverPresentationController is { } popover)
        {
            popover.SourceItem = _moreItem;
            if (popover.AdaptiveSheetPresentationController is { } sheet)
            {
                sheet.Detents = [UISheetPresentationControllerDetent.CreateMediumDetent(), UISheetPresentationControllerDetent.CreateLargeDetent()];
                sheet.PrefersGrabberVisible = true;
            }
        }
        Presenter.PresentViewController(navigation, true, null);
    }

    private void SetImportance(MessageImportance importance)
    {
        ViewModel.SelectedMessageImportance = importance;
        // Normal is the absence of an importance header, not a third value to write.
        ViewModel.IsImportanceSelected = importance != MessageImportance.Normal;
    }

    // ---- Navigation bar ----

    private void BuildNavigationItems()
    {
        _sendButton.PrimaryActionTriggered += (_, _) => Observe(SendOrUploadAsync());
        _sendButton.AccessibilityLabel = Translator.Buttons_Send;
        UpdateSendButton();
        _sendItem = new UIBarButtonItem(_sendButton) { AccessibilityLabel = Translator.Buttons_Send };
        _attachItem.Image = WinoIcons.Image(WinoIconGlyph.Attachment, 20, null, Translator.Composer_AttachFiles);
        _attachItem.Clicked += (_, _) => AttachFiles();
        _moreItem.Image = WinoIcons.Image(WinoIconGlyph.More, 20, null, Translator.More);
        _popOutItem.Image = WinoIcons.Image(WinoIconGlyph.OpenInNewWindow, 20, null, Translator.Buttons_PopOut);
        _popOutItem.Clicked += (_, _) =>
        {
            if (FrameHost is { } frame) Observe(frame.OpenInNewWindowAsync(this));
        };
        _moreItem.Menu = UIMenu.Create(Translator.More, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline,
            [UIDeferredMenuElement.CreateUncached(completion => completion(BuildMoreMenu()))]);
        _cancelItem = new UIBarButtonItem(Translator.Buttons_Cancel, UIBarButtonItemStyle.Plain, (_, _) => Observe(CloseAsync()));
        ApplyNavigationItems();
    }

    /// <summary>Send, More and Attach on the right (the shell's own reader items after them); Cancel on a sheet root.</summary>
    private void ApplyNavigationItems()
    {
        if (_sendItem is null) return;
        var item = NavigationItem;
        // The iPad shell puts its reader items on the page before activation; keep them after the composer's.
        _hostRightItems ??= (item.RightBarButtonItems ?? []).Where(existing => existing != _sendItem && existing != _moreItem && existing != _attachItem && existing != _popOutItem).ToArray();
        UIBarButtonItem[] popOut = FrameHost?.CanOpenInNewWindow == true ? [_popOutItem] : [];
        item.RightBarButtonItems = [_sendItem, _moreItem, _attachItem, .. popOut, .. _hostRightItems];
        if (IsSheetRoot && _cancelItem is not null) item.LeftBarButtonItem = _cancelItem;
        if (IsSheetRoot) item.Title = string.IsNullOrWhiteSpace(ViewModel.Subject) ? Translator.MailItemNoSubject : ViewModel.Subject;
    }

    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        ApplyNavigationItems();
    }

    /// <summary>Send for a synchronized draft; Send to server for a local draft that could not be uploaded (Mac pair of buttons).</summary>
    private void UpdateSendButton()
    {
        var upload = ViewModel.ShouldShowSendToServerButton && !ViewModel.ShouldShowSendButton;
        var configuration = UIButtonConfiguration.FilledButtonConfiguration;
        configuration.Title = upload ? Translator.Buttons_SendToServer : Translator.Buttons_Send;
        configuration.Image = WinoIcons.Image(WinoIconGlyph.Send, 14, UIColor.White, Translator.Buttons_Send);
        configuration.ImagePadding = 6;
        configuration.BaseBackgroundColor = WinoStyle.Accent;
        configuration.BaseForegroundColor = UIColor.White;
        configuration.CornerStyle = UIButtonConfigurationCornerStyle.Capsule;
        configuration.ButtonSize = UIButtonConfigurationSize.Small;
        _sendButton.Configuration = configuration;
        _sendButton.AccessibilityLabel = configuration.Title;
        _sendButton.AccessibilityHint = upload ? Translator.Composer_LocalDraftSyncInfo : null;
        _sendButton.Enabled = upload ? _sendToServerEnabled : _sendEnabled;
        _sendButton.SizeToFit();
    }

    private Task SendOrUploadAsync()
    {
        var upload = ViewModel.ShouldShowSendToServerButton && !ViewModel.ShouldShowSendButton;
        return upload ? UploadAsync() : SendAsync();
    }

    private async Task UploadAsync()
    {
        if (ViewModel.SendToServerCommand.CanExecute(null)) await ViewModel.SendToServerCommand.ExecuteAsync(null);
    }

    private void AttachFiles() => Observe(ViewModel.AttachFilesCommand.ExecuteAsync(null));

    // ---- Lifecycle ----

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        var editor = new WebKitHtmlMailEditorSession(_webView);
        _editor = editor;
        if (_launcher is not null) editor.Configure(_launcher);
        editor.OperationFailed += (_, exception) => ReportError(exception);
        editor.ContentChanged += (_, _) => ScheduleAutosave();
        editor.ContentChanged += (_, _) => _ = Dispatcher.ExecuteOnUIThread(RefreshFindAfterEdit);
        _formatToolbar?.Attach(editor);
        _darkEditor = WinoTraits.IsDark(TraitCollection);
        _appearanceDark = _darkEditor;
        var focusOnOpen = ConsumeInitialFocusRequest(parameter as Wino.Mail.ViewModels.Data.MailItemViewModel);

        ViewModel.GetHTMLBodyFunction = GetHtmlBodyAsync;
        ViewModel.RenderHtmlBodyAsyncFunc = RenderBodyAsync;
        ViewModel.CloseRequested += CloseRequested;
        ViewModel.ApplySignatureHtmlFunc = ApplySignatureHtmlAsync;
        ViewModel.RewriteErrorHandler = ShowRewriteError;
        InstallKeyCommands();
#if DEBUG
        RegisterComposeDebugCommands();
#endif

        Bind(nameof(ViewModel.Subject), vm => vm.Subject, subject =>
        {
            if ((_subjectField.Text ?? string.Empty) != (subject ?? string.Empty) && _subjectField.MarkedTextRange is null) _subjectField.Text = subject ?? string.Empty;
            if (IsSheetRoot) NavigationItem.Title = string.IsNullOrWhiteSpace(subject) ? Translator.MailItemNoSubject : subject;
        });
        Bind(nameof(ViewModel.IsCCBCCVisible), vm => vm.IsCCBCCVisible, visible =>
        {
            CcField.Hidden = !visible;
            BccField.Hidden = !visible;
            _ccBccButton.Hidden = visible;
        });
        Bind(nameof(ViewModel.AvailableAliases), vm => vm.AvailableAliases, _ => UpdateFrom());
        Bind(nameof(ViewModel.SelectedAlias), vm => vm.SelectedAlias, _ => UpdateFrom());
        Bind(nameof(ViewModel.ComposingAccount), vm => vm.ComposingAccount, _ => UpdateFrom());
        Bind(nameof(ViewModel.CurrentMailDraftItem), vm => vm.CurrentMailDraftItem, _ => UpdateSendButton());
        Bind(nameof(ViewModel.IsDraftBusy), vm => vm.IsDraftBusy, _ => { UpdateSendButton(); UpdateDraftStatus(); });
        Bind(nameof(ViewModel.IsDraftSyncFailed), vm => vm.IsDraftSyncFailed, _ => UpdateSyncFailedBar());
        Bind(nameof(ViewModel.DraftSyncErrorMessage), vm => vm.DraftSyncErrorMessage, _ => UpdateSyncFailedBar());
        Bindings.Own(new CommandBinding(ViewModel.SendCommand, () => null, enabled => { _sendEnabled = enabled; UpdateSendButton(); }, Dispatcher, ReportError));
        Bindings.Own(new CommandBinding(ViewModel.SendToServerCommand, () => null, enabled => { _sendToServerEnabled = enabled; UpdateSendButton(); }, Dispatcher, ReportError));
        ObserveRecipients(ViewModel.ToItems, ToField);
        ObserveRecipients(ViewModel.CCItems, CcField);
        ObserveRecipients(ViewModel.BCCItems, BccField);
        BindAttachments();
        EventHandler accentChanged = (_, _) => UpdateSendButton();
        WinoStyle.AccentChanged += accentChanged;
        Bindings.Own(new ActionDisposable(() => WinoStyle.AccentChanged -= accentChanged));
        BindRewrite();
        BindExtras();
        BindSecurityOptions();
        BindRecipientSuggestions();

        await editor.InitializeAsync();
        await editor.SetThemeAsync(_darkEditor);
        await Dispatcher.ExecuteOnUIThread(UpdateThemeButton);
        // A null parameter (an empty composer, for example from the debug bridge) leaves the fields empty.
        await ViewModel.InitializeNavigationAsync(mode, parameter);
        _ = ViewModel.RefreshRewriteAvailabilityAsync();
        await ApplyInitialFocusAsync(focusOnOpen);
    }

    protected override async Task DeactivateAsync()
    {
        _autosave?.Cancel();
        ViewModel.CloseRequested -= CloseRequested;
        ViewModel.RewriteSession.Reset();
        ViewModel.RenderHtmlBodyAsyncFunc = null;
        ViewModel.ApplySignatureHtmlFunc = null;
        ViewModel.RewriteErrorHandler = null;
        CloseSuggestions();
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
        if (_editor is not { } editor || _editorDisposed) return;
        _editorDisposed = true;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            _formatToolbar?.Detach();
            _webView.Accessory = null;
        });
        try { await editor.DisposeAsync(); }
        catch (Exception exception) { ReportError(exception); }
    }

    private async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { ReportError(exception); }
    }

    private void Run(EditorCommand command)
    {
        if (_editor is not { } editor || _editorDisposed) return;
        Observe(editor.ExecuteCommandAsync(command));
    }

    private void FocusBody()
    {
        if (_editor is not { } editor || _editorDisposed) return;
        _webView.BecomeFirstResponder();
        Observe(editor.FocusEditorAsync(true));
    }

    private async Task<string> GetHtmlBodyAsync()
    {
        if (_editor is not { } editor || _editorDisposed) return ViewModel.CurrentMimeMessage?.HtmlBody ?? string.Empty;
        try { return await editor.GetHtmlBodyAsync() ?? string.Empty; }
        catch (ObjectDisposedException) { return ViewModel.CurrentMimeMessage?.HtmlBody ?? string.Empty; }
    }

    /// <summary>Windows RenderComposeHtmlAsync: spell check, its language and autocorrect, typography, then the body.</summary>
    private async Task RenderBodyAsync(string html)
    {
        if (_editor is not { } editor || _editorDisposed) return;
        var preferences = ViewModel.PreferencesService;
        await editor.SetDefaultTypographyAsync(preferences.ComposerFont, preferences.ComposerFontSize);
        await editor.Session.ExecuteCommandAsync(EditorCommand.ToggleSpellCheck(preferences.IsComposerSpellCheckEnabled));
        if (!string.IsNullOrWhiteSpace(preferences.ComposerSpellCheckLanguageCode))
            await editor.Session.ExecuteCommandAsync(EditorCommand.SetSpellCheckLanguage(preferences.ComposerSpellCheckLanguageCode));
        await editor.Session.ExecuteCommandAsync(EditorCommand.ToggleAutoCorrect(preferences.IsComposerAutoCorrectEnabled));
        await editor.RenderHtmlAsync(html ?? string.Empty);
    }

    /// <summary>The From row: the alias menu (checked alias) when the account has several, otherwise the account address.</summary>
    private void UpdateFrom()
    {
        var account = ViewModel.ComposingAccount;
        var aliases = ViewModel.AvailableAliases;
        string title;
        if (aliases is { Count: > 0 })
        {
            var selected = ViewModel.SelectedAlias ?? aliases[0];
            title = AliasTitle(selected, account?.SenderName);
            var actions = aliases.Select(alias => (UIMenuElement)EditorFormatToolbar.Action(AliasTitle(alias, account?.SenderName),
                () => ViewModel.SelectedAlias = alias, ReferenceEquals(alias, selected))).ToArray();
            _fromButton.Menu = UIMenu.Create(FieldCaption(Translator.ComposerFrom), actions);
        }
        else
        {
            title = account is null ? string.Empty
                : string.IsNullOrWhiteSpace(account.SenderName) ? account.Address ?? string.Empty : $"{account.SenderName} <{account.Address}>";
            _fromButton.Menu = null;
        }
        _fromButton.Configuration = FromConfiguration(title);
        _fromButton.Enabled = aliases is { Count: > 1 };
        _fromButton.AccessibilityValue = title;
    }

    private static string AliasTitle(Wino.Core.Domain.Entities.Mail.MailAccountAlias alias, string? senderName)
    {
        var name = string.IsNullOrWhiteSpace(alias.AliasSenderName) ? senderName : alias.AliasSenderName;
        return string.IsNullOrWhiteSpace(name) ? alias.AliasAddress ?? string.Empty : $"{name} <{alias.AliasAddress}>";
    }

    private void UpdateDraftStatus()
    {
        if (ViewModel.IsDraftBusy)
        {
            _draftSpinner.StartAnimating();
            _draftStatus.Text = Translator.Composer_SavingDraft;
        }
        else
        {
            _draftSpinner.StopAnimating();
            _draftStatus.Text = _lastSavedStatus;
        }
    }

    /// <summary>Windows DraftSyncFailedInfoBar: open while the upload failed, with the server's error.</summary>
    private void UpdateSyncFailedBar()
    {
        var failed = ViewModel.IsDraftSyncFailed;
        _syncFailedBar.Message = ViewModel.DraftSyncErrorMessage;
        if (_syncFailedHost.Hidden == !failed) return;
        _syncFailedHost.Hidden = !failed;
        if (failed) UIAccessibility.PostNotification(UIAccessibilityPostNotification.Announcement, new Foundation.NSString($"{Translator.Draft_SyncFailedInfoBarTitle}. {ViewModel.DraftSyncErrorMessage}"));
    }

    private void ScheduleAutosave()
    {
        _autosave?.Cancel();
        var source = _autosave = new CancellationTokenSource();
        Observe(AutosaveAsync(source.Token));
    }

    private async Task AutosaveAsync(CancellationToken token)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(3), token); }
        catch (OperationCanceledException) { return; }
        if (token.IsCancellationRequested || _editorDisposed || Bindings.IsDisposed) return;
        await SyncAllRecipientsAsync();
        if (await ViewModel.UpdateMimeChangesAsync())
        {
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                _lastSavedStatus = string.Format(Translator.MacOS_Composer_DraftSaved, DateTime.Now.ToString("t", System.Globalization.CultureInfo.CurrentCulture));
                UpdateDraftStatus();
            });
        }
    }

    private async Task SendAsync()
    {
        await SyncAllRecipientsAsync();
        if (ViewModel.SendCommand.CanExecute(null)) await ViewModel.SendCommand.ExecuteAsync(null);
    }

    // ---- Closing ----

    /// <summary>Sent, discarded or deleted elsewhere: the ViewModel asks to close (no question asked).</summary>
    private void CloseRequested(object? sender, EventArgs args) => Observe(DismissComposerAsync());

    /// <summary>
    /// Cancel on the sheet: save the draft, discard it, or keep editing (iOS Mail). A composer without a draft (an empty
    /// debug composer) closes at once. Discard goes through the ViewModel without a second confirmation.
    /// </summary>
    private async Task CloseAsync()
    {
        if (_closing || !IsActive) return;
        _closing = true;
        try
        {
            CloseSuggestions();
            if (ViewModel.CurrentMailDraftItem is null || ViewModel.ComposingAccount is null || _dialogs is not { } dialogs)
            {
                await DismissComposerAsync();
                return;
            }
            var choice = await dialogs.ShowThreeButtonDialogAsync(Translator.Draft, Translator.Composer_CloseDraftMessage,
                Translator.Buttons_Save, Translator.Buttons_Discard, Translator.Buttons_Cancel, WinoCustomMessageDialogIcon.Question);
            switch (choice)
            {
                case ThreeButtonDialogResult.Primary:
                    await SyncAllRecipientsAsync();
                    await ViewModel.UpdateMimeChangesAsync();
                    await DismissComposerAsync();
                    break;
                case ThreeButtonDialogResult.Secondary:
                    _autosave?.Cancel();
                    await ViewModel.DiscardDraftAsync(false);
                    await DismissComposerAsync();
                    break;
            }
        }
        finally { _closing = false; }
    }

    private async Task DismissComposerAsync()
    {
        if (CloseHandler is { } handler)
        {
            await handler();
            return;
        }
        Task dismissal = Task.CompletedTask;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (IsSheetRoot && NavigationController is { } sheet && _shellHost is { } host) dismissal = host.DismissSheetAsync(sheet);
            else WeakReferenceMessenger.Default.Send(new Wino.Messaging.Client.Mails.DisposeRenderingFrameRequested());
        });
        await dismissal;
    }

    // ---- Initial focus (Windows ApplyInitialFocusAsync) ----

    private static bool ConsumeInitialFocusRequest(Wino.Mail.ViewModels.Data.MailItemViewModel? draft)
    {
        if (draft is not { ShouldFocusComposerOnOpen: true }) return false;
        draft.ShouldFocusComposerOnOpen = false;
        return true;
    }

    /// <summary>A reply (it has In-Reply-To) starts in the body; anything else starts in To.</summary>
    private bool ShouldFocusEditor()
    {
        var mime = ViewModel.CurrentMimeMessage;
        var inReplyTo = mime?.InReplyTo;
        if (string.IsNullOrWhiteSpace(inReplyTo)) inReplyTo = ViewModel.CurrentMailDraftItem?.MailCopy?.InReplyTo;
        if (string.IsNullOrWhiteSpace(inReplyTo) && mime is not null && mime.Headers.Contains(HeaderId.InReplyTo))
            inReplyTo = mime.Headers[HeaderId.InReplyTo];
        return !string.IsNullOrWhiteSpace(inReplyTo);
    }

    private async Task ApplyInitialFocusAsync(bool requested)
    {
        if (_editor is not { } editor || _editorDisposed) return;
        var focusEditor = false;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            // Never steal focus from the find bar.
            if (IsFindVisible) return;
            if (requested && ShouldFocusEditor())
            {
                focusEditor = true;
                _webView.BecomeFirstResponder();
            }
            else if (requested || (ViewModel.ToItems.Count == 0 && ViewModel.CurrentMailDraftItem is not null))
            {
                ToField.Focus();
            }
        });
        if (focusEditor) await editor.FocusEditorAsync(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _autosave?.Cancel();
            ViewModel.CloseRequested -= CloseRequested;
            DisposeRecipients();
            DisposeAttachments();
            DisposeToolbar();
            if (!_editorDisposed && _editor is not null) _ = DisposeEditorAsync();
        }
        base.Dispose(disposing);
    }
}
