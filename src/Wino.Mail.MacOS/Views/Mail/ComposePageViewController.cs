using AppKit;
using CommunityToolkit.Mvvm.Messaging;
using CoreGraphics;
using Foundation;
using MimeKit;
using WebKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Editor;
using Wino.Editor.AppKit;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.MacOS.Views.Mail.Compose;
using Wino.Mail.ViewModels;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// Composer in the reading pane (design boards "Compose" and "Compose window"): the draft-upload failure
/// bar, the command row with the filled Send button, From pop-up, To/Cc/Bcc token fields with Cc/Bcc
/// reveal and the recipient suggestion popup, Subject, the shared Format / Insert / Options editor toolbar
/// driving the HTML editor in a WKWebView, and the attachment tray. "Open in new window" moves this same
/// controller and draft into a 960x720 window and back; closing that window keeps the draft.
/// Partials: Toolbar (editor toolbar, theme, Send shortcut), Recipients, Attachments, Rewrite (Wino
/// Intelligence), Extras (templates and signature), Options (S/MIME and read receipt), Find (find and
/// replace bar, composer shortcuts, IME guards), Drop and Debug.
/// </summary>
public sealed partial class ComposePageViewController : WinoViewController<ComposePageViewModel>, IReadingPaneChild
{
    private static readonly HashSet<ComposePageViewController> Detached = new();
    /// <summary>Composer font vocabulary, shared with the signature editor sheet.</summary>
    internal static readonly string[] Fonts = EditorFormatToolbar.Fonts;
    internal static readonly int[] FontSizes = EditorFormatToolbar.FontSizes;

    private readonly IExternalLauncher _launcher;
    private readonly AppKitNavigationService _navigation;
    private readonly ITranslationService _translations;
    private readonly IKeyboardShortcutService _shortcuts;
    private readonly IPictureStorageService _pictures;
    private AppKitHtmlMailEditorSession? _editor;
    private WKWebView _webView = null!;
    private WinoInfoBar _syncFailedBar = null!;
    private NSButton _sendButton = null!;
    private NSButton _sendToServerButton = null!;
    private NSPopUpButton _importance = null!;
    private NSTextField _draftStatus = null!;
    private NSProgressIndicator _draftSpinner = null!;
    private NSButton _popOutButton = null!;
    private NSPopUpButton _fromPopup = null!;
    private NSTokenField _toField = null!;
    private NSTokenField _ccField = null!;
    private NSTokenField _bccField = null!;
    private NSView _ccRow = null!;
    private NSView _bccRow = null!;
    private NSView _ccBccButtons = null!;
    private NSTextField _subjectField = null!;
    private NSWindow? _window;
    private IReadingPaneHost? _lastHost;
    private CancellationTokenSource? _autosave;
    private bool _editorDisposed;
    private bool _docking;
    private string _lastSavedStatus = string.Empty;

    public ComposePageViewController(ComposePageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger, IExternalLauncher launcher, AppKitNavigationService navigation,
        ITranslationService translations, IKeyboardShortcutService shortcuts, IPictureStorageService pictures)
        : base(viewModel, dispatcher, logger)
    {
        _launcher = launcher;
        _navigation = navigation;
        _translations = translations;
        _shortcuts = shortcuts;
        _pictures = pictures;
    }

    public IReadingPaneHost? PaneHost
    {
        get => _lastHost;
        set { if (value is not null) _lastHost = value; else if (_window is null) _lastHost = null; UpdatePopOutButton(); }
    }

    public override void LoadView()
    {
        var root = new ComposeRootView();
        root.AppearanceChanged += (_, _) => FollowAppearance();

        // ---- Draft upload failure (Windows DraftSyncFailedInfoBar) ----
        _syncFailedBar = new WinoInfoBar(WinoInfoBarSeverity.Warning, Translator.Draft_SyncFailedInfoBarTitle)
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            IsClosable = false,
            ActionTitle = Translator.Draft_RetryUpload
        };
        _syncFailedBar.ActionInvoked += (_, _) =>
        {
            if (ViewModel.SendToServerCommand.CanExecute(null)) Observe(ViewModel.SendToServerCommand.ExecuteAsync(null));
        };
        var syncFailedHost = new NSView { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        WinoLayout.Fill(_syncFailedBar, syncFailedHost, 10, 12, 0, 12);

        // ---- Command row ----
        _sendButton = new NSButton { Title = Translator.Buttons_Send, BezelStyle = NSBezelStyle.Rounded, TranslatesAutoresizingMaskIntoConstraints = false };
        _sendButton.Image = WinoIcons.Image(WinoIconGlyph.Send, 14, null, Translator.Buttons_Send);
        _sendButton.ImagePosition = NSCellImagePosition.ImageLeading;
        _sendButton.BezelColor = WinoStyle.Accent;
        _sendButton.KeyEquivalent = "\r";
        _sendButton.KeyEquivalentModifierMask = NSEventModifierMask.CommandKeyMask;
        _sendButton.Activated += (_, _) => Observe(SendAsync());
        _sendToServerButton = CommandButton(Translator.Buttons_SendToServer, ViewModel.SendToServerCommand);
        _sendToServerButton.Hidden = true;
        _sendToServerButton.ToolTip = Translator.Composer_LocalDraftSyncInfo;
        WinoAccessibility.Help(_sendToServerButton, Translator.Composer_LocalDraftSyncInfo);
        var attach = ToolbarButton(WinoIconGlyph.Attachment, Translator.ComposerAttachmentsDragDropAttach_Message, () => Observe(ViewModel.AttachFilesCommand.ExecuteAsync(null)), showTitle: true);
        WinoAccessibility.Help(attach, Translator.Composer_AttachFilesDescription);
        var signature = BuildSignaturePicker();
        var template = BuildTemplateButton();
        var rewrite = BuildRewriteButton();

        _importance = new NSPopUpButton { PullsDown = false, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false };
        _importance.AddItems([Translator.Composer_HighImportance, Translator.Composer_NormalImportance, Translator.Composer_LowImportance]);
        _importance.SelectItem(1);
        _importance.ToolTip = Translator.Composer_ImportanceDescription;
        WinoAccessibility.Label(_importance, Translator.Composer_Importance);
        _importance.Activated += (_, _) => SetImportance(_importance.IndexOfSelectedItem switch { 0 => MessageImportance.High, 2 => MessageImportance.Low, _ => MessageImportance.Normal });

        // Draft autosave: a spinner with "Saving draft..." while busy, otherwise when it was last saved.
        _draftSpinner = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, ControlSize = NSControlSize.Small, IsDisplayedWhenStopped = false, Indeterminate = true, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(_draftSpinner, 14, 14);
        _draftStatus = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
        _draftStatus.LineBreakMode = NSLineBreakMode.TruncatingTail;
        _draftStatus.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _popOutButton = ToolbarButton(WinoIconGlyph.OpenInNewWindow, Translator.Buttons_PopOut, TogglePopOut);
        var discard = ToolbarButton(WinoIconGlyph.Delete, Translator.Buttons_Discard, () => Observe(ViewModel.DiscardCommand.ExecuteAsync(null)));
        var commandRow = WinoLayout.HStack(8, _sendButton, _sendToServerButton, attach, signature, template, rewrite, WinoLayout.Spacer(), _draftSpinner, _draftStatus, _popOutButton, discard);
        commandRow.SetCustomSpacing(4, _draftSpinner);
        commandRow.AccessibilityElement = true;
        commandRow.AccessibilityRole = NSAccessibilityRoles.ToolbarRole;
        WinoAccessibility.Label(commandRow, Translator.Composer_CommandBarLabel);
        commandRow.EdgeInsets = new NSEdgeInsets(10, 16, 10, 12);

        // ---- Header fields ----
        _fromPopup = new NSPopUpButton { PullsDown = false, Bordered = false, TranslatesAutoresizingMaskIntoConstraints = false };
        _fromPopup.Activated += (_, _) =>
        {
            int index = (int)_fromPopup.IndexOfSelectedItem;
            if (ViewModel.AvailableAliases is { } aliases && index >= 0 && index < aliases.Count) ViewModel.SelectedAlias = aliases[index];
        };
        _recipientDelegate = new RecipientDelegate(this);
        WinoAccessibility.Label(_fromPopup, Translator.ComposerFrom.Trim().TrimEnd(':'));
        _toField = TokenField(Translator.ComposerTo.Trim().TrimEnd(':'));
        _ccField = TokenField("Cc");
        _bccField = TokenField("Bcc");
        var ccButton = TextButton("Cc", () => { ViewModel.IsCCBCCVisible = true; Window()?.MakeFirstResponder(_ccField); });
        var bccButton = TextButton("Bcc", () => { ViewModel.IsCCBCCVisible = true; Window()?.MakeFirstResponder(_bccField); });
        WinoAccessibility.Label(ccButton, Translator.Composer_CcBcc);
        WinoAccessibility.Label(bccButton, Translator.Composer_CcBcc);
        _ccBccButtons = WinoLayout.HStack(2, ccButton, bccButton);
        _subjectField = new NSTextField { Bordered = false, DrawsBackground = false, Font = WinoStyle.Body, FocusRingType = NSFocusRingType.None, TranslatesAutoresizingMaskIntoConstraints = false };
        _subjectField.PlaceholderString = Translator.ComposerSubject.Trim().TrimEnd(':');
        _subjectField.Changed += (_, _) =>
        {
            // The subject follows committed text only: an input method's marked text is still being composed.
            if (HasMarkedText(_subjectField.CurrentEditor)) return;
            ViewModel.Subject = _subjectField.StringValue;
            ScheduleAutosave();
        };
        _subjectField.DoCommandBySelector = SubjectCommand;
        WinoAccessibility.Label(_subjectField, Translator.ComposerSubject.Trim().TrimEnd(':'));

        var fromRow = FieldRow(Translator.ComposerFrom.Trim().TrimEnd(':'), _fromPopup);
        var toRow = FieldRow(Translator.ComposerTo.Trim().TrimEnd(':'), _toField, _ccBccButtons);
        _ccRow = FieldRow("Cc", _ccField);
        _bccRow = FieldRow("Bcc", _bccField);
        var subjectRow = FieldRow(Translator.ComposerSubject.Trim().TrimEnd(':'), _subjectField);
        var fields = WinoLayout.VStack(0, fromRow, toRow, _ccRow, _bccRow, subjectRow);
        foreach (var row in fields.ArrangedSubviews) row.WidthAnchor.ConstraintEqualTo(fields.WidthAnchor).Active = true;

        // ---- Editor toolbar ----
        var editorToolbar = BuildEditorToolbar();

        // ---- Editor ----
        _webView = new WKWebView(CGRect.Empty, new WKWebViewConfiguration()) { TranslatesAutoresizingMaskIntoConstraints = false };
        _webView.SetValueForKey(NSNumber.FromBoolean(false), new NSString("drawsBackground"));
        WinoAccessibility.Label(_webView, Translator.Reader_MessageBodyAutomationName);
        var editorHost = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(_webView, editorHost, 6, 12, 0, 12);
        editorHost.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);

        // ---- Attachment tray ----
        BuildAttachmentTray();

        var stack = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Spacing = 0,
            Alignment = NSLayoutAttribute.Leading,
            Distribution = NSStackViewDistribution.Fill,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        var rewriteStrip = BuildRewriteStrip();
        var findBar = BuildFindBar();
        foreach (var view in new NSView[] { syncFailedHost, commandRow, new WinoSeparator(), fields, rewriteStrip, new WinoSeparator(), editorToolbar, new WinoSeparator(), findBar, editorHost, _attachmentHost })
        {
            stack.AddArrangedSubview(view);
            view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        }
        WinoLayout.Fill(stack, root);
        View = root;
        SetUpFileDrop();
        ConfigureKeyViewLoop();
    }

    // ---- View helpers ----

    private NSWindow? Window() => View.Window;

    private NSView FieldRow(string label, params NSView[] content)
    {
        var caption = WinoStyle.Label(label, WinoStyle.Body, WinoStyle.SecondaryText);
        caption.Alignment = NSTextAlignment.Right;
        WinoLayout.Size(caption, 52, -1);
        var views = new List<NSView> { caption };
        views.AddRange(content);
        var row = WinoLayout.HStack(10, views.ToArray());
        row.EdgeInsets = new NSEdgeInsets(6, 16, 6, 16);
        row.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Vertical);
        content[0].SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        var host = WinoLayout.VStack(0, row, new WinoSeparator());
        foreach (var view in host.ArrangedSubviews) view.WidthAnchor.ConstraintEqualTo(host.WidthAnchor).Active = true;
        return host;
    }

    private NSButton ToolbarButton(WinoIconGlyph glyph, string label, Action action, bool showTitle = false)
    {
        var button = new NSButton
        {
            Bordered = false,
            BezelStyle = NSBezelStyle.Inline,
            Title = showTitle ? label : string.Empty,
            Image = WinoIcons.Image(glyph, 14, null, label),
            ImagePosition = showTitle ? NSCellImagePosition.ImageLeading : NSCellImagePosition.ImageOnly,
            ContentTintColor = WinoStyle.SecondaryText,
            ToolTip = label,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoAccessibility.Label(button, label);
        if (!showTitle) WinoLayout.Size(button, 26, 24);
        button.Activated += (_, _) => action();
        return button;
    }

    private static NSButton TextButton(string title, Action action)
    {
        var button = new NSButton { Title = title, Bordered = false, BezelStyle = NSBezelStyle.Inline, Font = NSFont.SystemFontOfSize(12), ContentTintColor = WinoStyle.SecondaryText, TranslatesAutoresizingMaskIntoConstraints = false };
        button.Activated += (_, _) => action();
        return button;
    }

    private void SetImportance(MessageImportance importance)
    {
        ViewModel.SelectedMessageImportance = importance;
        // Normal is the absence of an importance header, not a third value to write.
        ViewModel.IsImportanceSelected = importance != MessageImportance.Normal;
    }

    // ---- Lifecycle ----

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        _editor = new AppKitHtmlMailEditorSession(_webView);
        _editor.Configure(_launcher);
        _editor.OperationFailed += (_, exception) => ReportError(exception);
        _editor.ContentChanged += (_, _) => ScheduleAutosave();
        _formatToolbar.Attach(_editor);
        _darkEditor = WinoIcons.IsDark(View.EffectiveAppearance);
        _appearanceDark = _darkEditor;
        var focusOnOpen = ConsumeInitialFocusRequest(parameter as Wino.Mail.ViewModels.Data.MailItemViewModel);

        ViewModel.GetHTMLBodyFunction = GetHtmlBodyAsync;
        ViewModel.RenderHtmlBodyAsyncFunc = RenderBodyAsync;
        ViewModel.CloseRequested += CloseRequested;
        ViewModel.ApplySignatureHtmlFunc = ApplySignatureHtmlAsync;
        ViewModel.RewriteErrorHandler = ShowRewriteError;
        _editor.ContentChanged += (_, _) => _ = Dispatcher.ExecuteOnUIThread(RefreshFindAfterEdit);
        InstallKeyMonitor();
#if DEBUG
        RegisterComposeDebugCommands();
        // "compose-test ADDRESS SUBJECT…" sets the only To recipient and the subject; "compose-send" sends.
        Infrastructure.MacDebugBridge.Register("compose-test", async args =>
        {
            var contact = await ViewModel.GetAddressInformationAsync(args[0], ViewModel.ToItems);
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                ViewModel.ToItems.Clear();
                ViewModel.TryAddRecipient(ViewModel.ToItems, contact);
                _subjectField.StringValue = string.Join(' ', args.Skip(1));
                ViewModel.Subject = _subjectField.StringValue;
            });
            return $"to={ViewModel.ToItems.Count} subject='{ViewModel.Subject}' canSend={ViewModel.SendCommand.CanExecute(null)}";
        });
        Infrastructure.MacDebugBridge.Register("compose-send", async _ => { await SendAsync(); return "ok"; });
#endif

        Bind(nameof(ViewModel.Subject), vm => vm.Subject, subject =>
        {
            if (_subjectField.StringValue != (subject ?? string.Empty)) _subjectField.StringValue = subject ?? string.Empty;
            if (_window is not null) _window.Title = string.IsNullOrWhiteSpace(subject) ? Translator.MailItemNoSubject : subject;
        });
        Bind(nameof(ViewModel.IsCCBCCVisible), vm => vm.IsCCBCCVisible, visible =>
        {
            _ccRow.Hidden = !visible;
            _bccRow.Hidden = !visible;
            _ccBccButtons.Hidden = visible;
        });
        Bind(nameof(ViewModel.AvailableAliases), vm => vm.AvailableAliases, _ => UpdateFrom());
        Bind(nameof(ViewModel.SelectedAlias), vm => vm.SelectedAlias, _ => UpdateFrom());
        Bind(nameof(ViewModel.ComposingAccount), vm => vm.ComposingAccount, _ => UpdateFrom());
        Bind(nameof(ViewModel.CurrentMailDraftItem), vm => vm.CurrentMailDraftItem, _ => UpdateSendButtons());
        Bind(nameof(ViewModel.IsDraftBusy), vm => vm.IsDraftBusy, _ => { UpdateSendButtons(); UpdateDraftStatus(); });
        Bind(nameof(ViewModel.IsDraftSyncFailed), vm => vm.IsDraftSyncFailed, _ => UpdateSyncFailedBar());
        Bind(nameof(ViewModel.DraftSyncErrorMessage), vm => vm.DraftSyncErrorMessage, _ => UpdateSyncFailedBar());
        Bind(nameof(ViewModel.SelectedMessageImportance), vm => vm.SelectedMessageImportance, importance =>
            _importance.SelectItem(importance switch { MessageImportance.High => 0, MessageImportance.Low => 2, _ => 1 }));
        Bindings.Own(new CommandBinding(ViewModel.SendCommand, () => null, enabled => _sendButton.Enabled = enabled, Dispatcher, ReportError));
        ObserveRecipients(ViewModel.ToItems, _toField);
        ObserveRecipients(ViewModel.CCItems, _ccField);
        ObserveRecipients(ViewModel.BCCItems, _bccField);
        BindAttachments();
        EventHandler accentChanged = (_, _) => _sendButton.BezelColor = WinoStyle.Accent;
        WinoStyle.AccentChanged += accentChanged;
        Bindings.Own(new ActionDisposable(() => WinoStyle.AccentChanged -= accentChanged));
        UpdatePopOutButton();
        BindRewrite();
        BindExtras();
        BindSecurityOptions();
        BindSendShortcut();
        BindRecipientPopup();

        await _editor.InitializeAsync();
        await _editor.SetThemeAsync(_darkEditor);
        await Dispatcher.ExecuteOnUIThread(UpdateThemeButton);
        await ViewModel.InitializeNavigationAsync(mode, parameter!);
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
        RemoveKeyMonitor();
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
            ViewModel.OnNavigatedFrom(NavigationMode.New, null!);
        }
    }

    private async Task DisposeEditorAsync()
    {
        if (_editor is null || _editorDisposed) return;
        _editorDisposed = true;
        await Dispatcher.ExecuteOnUIThread(() => _formatToolbar?.Detach());
        try { await _editor.DisposeAsync(); }
        catch (Exception exception) { ReportError(exception); }
    }

    private void Bind<TValue>(string property, Func<ComposePageViewModel, TValue> read, Action<TValue> apply)
        => Bindings.Own(new PropertyBinding<ComposePageViewModel, TValue>(ViewModel, property, read, apply, Dispatcher, ReportError));

    private async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { ReportError(exception); }
    }

    private void Run(EditorCommand command)
    {
        if (_editor is null || _editorDisposed) return;
        Observe(_editor.ExecuteCommandAsync(command));
    }

    private async Task<string> GetHtmlBodyAsync()
    {
        if (_editor is null || _editorDisposed) return ViewModel.CurrentMimeMessage?.HtmlBody ?? string.Empty;
        try { return await _editor.GetHtmlBodyAsync() ?? string.Empty; }
        catch (ObjectDisposedException) { return ViewModel.CurrentMimeMessage?.HtmlBody ?? string.Empty; }
    }

    /// <summary>Windows RenderComposeHtmlAsync: spell check, its language and autocorrect, typography, then the body.</summary>
    private async Task RenderBodyAsync(string html)
    {
        if (_editor is null || _editorDisposed) return;
        var preferences = ViewModel.PreferencesService;
        await _editor.SetDefaultTypographyAsync(preferences.ComposerFont, preferences.ComposerFontSize);
        await _editor.Session.ExecuteCommandAsync(EditorCommand.ToggleSpellCheck(preferences.IsComposerSpellCheckEnabled));
        if (!string.IsNullOrWhiteSpace(preferences.ComposerSpellCheckLanguageCode))
            await _editor.Session.ExecuteCommandAsync(EditorCommand.SetSpellCheckLanguage(preferences.ComposerSpellCheckLanguageCode));
        await _editor.Session.ExecuteCommandAsync(EditorCommand.ToggleAutoCorrect(preferences.IsComposerAutoCorrectEnabled));
        await _editor.RenderHtmlAsync(html ?? string.Empty);
    }

    private void UpdateFrom()
    {
        _fromPopup.RemoveAllItems();
        var account = ViewModel.ComposingAccount;
        var aliases = ViewModel.AvailableAliases;
        if (aliases is { Count: > 0 })
        {
            foreach (var alias in aliases)
            {
                var name = string.IsNullOrWhiteSpace(alias.AliasSenderName) ? account?.SenderName : alias.AliasSenderName;
                _fromPopup.AddItem(string.IsNullOrWhiteSpace(name) ? alias.AliasAddress : $"{name} <{alias.AliasAddress}>");
            }
            int selected = ViewModel.SelectedAlias is null ? 0 : aliases.IndexOf(ViewModel.SelectedAlias);
            if (selected >= 0) _fromPopup.SelectItem(selected);
        }
        else if (account is not null)
        {
            _fromPopup.AddItem(string.IsNullOrWhiteSpace(account.SenderName) ? account.Address : $"{account.SenderName} <{account.Address}>");
        }
        _fromPopup.Enabled = aliases is { Count: > 1 };
    }

    private void UpdateSendButtons()
    {
        _sendButton.Hidden = !ViewModel.ShouldShowSendButton;
        _sendToServerButton.Hidden = !ViewModel.ShouldShowSendToServerButton;
    }

    private void UpdateDraftStatus()
    {
        if (ViewModel.IsDraftBusy)
        {
            _draftSpinner.StartAnimation(null);
            _draftStatus.StringValue = Translator.Composer_SavingDraft;
        }
        else
        {
            _draftSpinner.StopAnimation(null);
            _draftStatus.StringValue = _lastSavedStatus;
        }
    }

    /// <summary>Windows DraftSyncFailedInfoBar: open while the upload failed, with the server's error.</summary>
    private void UpdateSyncFailedBar()
    {
        var host = _syncFailedBar.Superview;
        if (host is null) return;
        var failed = ViewModel.IsDraftSyncFailed;
        _syncFailedBar.Message = ViewModel.DraftSyncErrorMessage;
        if (host.Hidden == !failed) return;
        host.Hidden = !failed;
        if (failed)
            NSAccessibility.PostNotification(_syncFailedBar, new NSString("AXAnnouncementRequested"),
                NSDictionary.FromObjectAndKey(new NSString($"{Translator.Draft_SyncFailedInfoBarTitle}. {ViewModel.DraftSyncErrorMessage}"), NSAccessibilityNotificationUserInfoKeys.AnnouncementKey));
    }

    private void UpdatePopOutButton()
    {
        if (_popOutButton is null) return;
        bool docked = _window is null;
        _popOutButton.Image = WinoIcons.Image(docked ? WinoIconGlyph.OpenInNewWindow : WinoIconGlyph.PanelLeft, 14, null, Translator.Buttons_PopOut);
        _popOutButton.Hidden = !docked && (_lastHost is null || !_lastHost.IsAvailable);
        if (docked && _lastHost is null) _popOutButton.Hidden = true;
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

    private void CloseRequested(object? sender, EventArgs args)
        => _ = Dispatcher.ExecuteOnUIThread(() =>
        {
            if (_window is not null)
            {
                _window.Close();
                return;
            }
            WeakReferenceMessenger.Default.Send(new Wino.Messaging.Client.Mails.DisposeRenderingFrameRequested());
        });

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
        var inReplyTo = ViewModel.CurrentMimeMessage?.InReplyTo;
        if (string.IsNullOrWhiteSpace(inReplyTo)) inReplyTo = ViewModel.CurrentMailDraftItem?.MailCopy?.InReplyTo;
        if (string.IsNullOrWhiteSpace(inReplyTo) && ViewModel.CurrentMimeMessage?.Headers.Contains(HeaderId.InReplyTo) == true)
            inReplyTo = ViewModel.CurrentMimeMessage.Headers[HeaderId.InReplyTo];
        return !string.IsNullOrWhiteSpace(inReplyTo);
    }

    private async Task ApplyInitialFocusAsync(bool requested)
    {
        if (_editor is null || _editorDisposed) return;
        var focusEditor = false;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            // Never steal focus from the find bar.
            if (IsFindVisible) return;
            if (requested && ShouldFocusEditor())
            {
                focusEditor = true;
                Window()?.MakeFirstResponder(_webView);
            }
            else if (requested || ViewModel.ToItems.Count == 0)
            {
                Window()?.MakeFirstResponder(_toField);
            }
        });
        if (focusEditor) await _editor.FocusEditorAsync(true);
    }

    // ---- Pop-out window ----

    private void TogglePopOut()
    {
        if (_window is null) Observe(PopOutAsync());
        else Observe(DockAsync());
    }

    internal Task PopOutFromHostAsync() => PopOutAsync(); // Reply from a popped-out reader (MailListPageViewController.ReadingPane.cs).
    private async Task PopOutAsync()
    {
        var host = _lastHost;
        if (host is null || !host.IsAvailable) return;
        CloseSuggestions();
        await host.PopOutAsync(this);
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            var window = new NSWindow(new CGRect(0, 0, 960, 720),
                NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable | NSWindowStyle.Miniaturizable,
                NSBackingStore.Buffered, false)
            {
                ReleasedWhenClosed = false,
                Title = string.IsNullOrWhiteSpace(ViewModel.Subject) ? Translator.MailItemNoSubject : ViewModel.Subject,
                ContentMinSize = new CGSize(640, 480)
            };
            window.ContentViewController = this;
            window.SetContentSize(new CGSize(960, 720));
            window.Center();
            window.WillClose += WindowWillClose;
            _window = window;
            Detached.Add(this);
            UpdatePopOutButton();
            window.MakeKeyAndOrderFront(null);
        });
    }

    private async Task DockAsync()
    {
        var host = _lastHost;
        var window = _window;
        if (host is null || !host.IsAvailable || window is null) return;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            CloseSuggestions();
            _docking = true;
            window.WillClose -= WindowWillClose;
            window.ContentViewController = null!;
            window.Close();
            _window = null;
            Detached.Remove(this);
            _docking = false;
            UpdatePopOutButton();
        });
        await host.DockAsync(this);
    }

    private async void WindowWillClose(object? sender, EventArgs args)
    {
        if (_docking) return;
        var window = _window;
        _window = null;
        if (window is not null) window.WillClose -= WindowWillClose;
        try
        {
            // Closing the window keeps the draft: releasing saves it, nothing is discarded.
            await ReleaseAsync();
        }
        catch (Exception exception) { ReportError(exception); }
        finally
        {
            Detached.Remove(this);
            if (window is not null) window.ContentViewController = null!;
            Dispose();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _autosave?.Cancel();
            ViewModel.CloseRequested -= CloseRequested;
            RemoveKeyMonitor();
            DisposeExtras();
            DisposeRecipients();
            DisposeAttachments();
            DisposeToolbar();
            if (!_editorDisposed && _editor is not null) _ = DisposeEditorAsync();
        }
        base.Dispose(disposing);
    }

    /// <summary>The composer's root view; reports appearance changes (system or custom theme) to the controller.</summary>
    private sealed class ComposeRootView : NSView
    {
        public event EventHandler? AppearanceChanged;

        public override void ViewDidChangeEffectiveAppearance()
        {
            base.ViewDidChangeEffectiveAppearance();
            AppearanceChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
