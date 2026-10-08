using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AppKit;
using CommunityToolkit.Mvvm.Messaging;
using CoreGraphics;
using Foundation;
using MimeKit;
using WebKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Editor;
using Wino.Editor.AppKit;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// Composer in the reading pane (design boards "Compose" and "Compose window"): command row with
/// the filled Send button, From pop-up, To/Cc/Bcc token fields with Cc/Bcc reveal and contact
/// completion, Subject, the in-content editor toolbar (Format / Insert / Options) driving the
/// shared HTML editor in a WKWebView, and the attachment tray. "Open in new window" moves this
/// same controller and draft into a 960x720 window and back; closing that window keeps the draft.
/// </summary>
public sealed class ComposePageViewController : WinoViewController<ComposePageViewModel>, IReadingPaneChild
{
    private static readonly HashSet<ComposePageViewController> Detached = new();
    /// <summary>Composer font vocabulary, shared with the signature editor sheet.</summary>
    internal static readonly string[] Fonts = ["Helvetica Neue", "Helvetica", "Arial", "Times New Roman", "Georgia", "Verdana", "Courier New"];
    internal static readonly int[] FontSizes = [8, 9, 10, 11, 12, 13, 14, 16, 18, 20, 24, 28, 32, 48];

    private readonly IExternalLauncher _launcher;
    private readonly Dictionary<string, string> _recipientNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RecipientSuggestion> _suggestions = new();
    private AppKitHtmlMailEditorSession? _editor;
    private WKWebView _webView = null!;
    private NSButton _sendButton = null!;
    private NSButton _sendToServerButton = null!;
    private NSPopUpButton _importance = null!;
    private NSTextField _draftStatus = null!;
    private NSButton _popOutButton = null!;
    private NSPopUpButton _fromPopup = null!;
    private NSTokenField _toField = null!;
    private NSTokenField _ccField = null!;
    private NSTokenField _bccField = null!;
    private NSView _ccRow = null!;
    private NSView _bccRow = null!;
    private NSView _ccBccButtons = null!;
    private NSTextField _subjectField = null!;
    private NSSegmentedControl _toolbarTabs = null!;
    private NSStackView _formatGroup = null!;
    private NSStackView _insertGroup = null!;
    private NSStackView _optionsGroup = null!;
    private NSButton _boldButton = null!;
    private NSButton _italicButton = null!;
    private NSButton _underlineButton = null!;
    private NSPopUpButton _fontPopup = null!;
    private NSPopUpButton _sizePopup = null!;
    private NSStackView _attachmentTray = null!;
    private NSView _attachmentHost = null!;
    private RecipientDelegate? _recipientDelegate;
    private NSWindow? _window;
    private IReadingPaneHost? _lastHost;
    private CancellationTokenSource? _autosave;
    private bool _editorDisposed;
    private bool _docking;
    private bool _syncingRecipients;
    private bool _darkEditor;
    private string _suggestionQuery = string.Empty;

    public ComposePageViewController(ComposePageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger, IExternalLauncher launcher)
        : base(viewModel, dispatcher, logger)
    {
        _launcher = launcher;
    }

    public IReadingPaneHost? PaneHost
    {
        get => _lastHost;
        set { if (value is not null) _lastHost = value; else if (_window is null) _lastHost = null; UpdatePopOutButton(); }
    }

    public override void LoadView()
    {
        var root = new NSView();

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
        var attach = ToolbarButton(WinoIconGlyph.Attachment, Translator.ComposerAttachmentsDragDropAttach_Message, () => Observe(ViewModel.AttachFilesCommand.ExecuteAsync(null)), showTitle: true);

        _importance = new NSPopUpButton { PullsDown = false, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false };
        _importance.AddItems([Translator.Composer_HighImportance, Translator.Composer_NormalImportance, Translator.Composer_LowImportance]);
        _importance.SelectItem(1);
        _importance.ToolTip = Translator.Composer_Importance;
        WinoAccessibility.Label(_importance, Translator.Composer_Importance);
        _importance.Activated += (_, _) =>
        {
            var importance = _importance.IndexOfSelectedItem switch { 0 => MessageImportance.High, 2 => MessageImportance.Low, _ => MessageImportance.Normal };
            ViewModel.SelectedMessageImportance = importance;
            ViewModel.IsImportanceSelected = importance != MessageImportance.Normal;
        };

        _draftStatus = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
        _popOutButton = ToolbarButton(WinoIconGlyph.OpenInNewWindow, Translator.Buttons_PopOut, TogglePopOut);
        var discard = ToolbarButton(WinoIconGlyph.Delete, Translator.Buttons_Discard, () => Observe(ViewModel.DiscardCommand.ExecuteAsync(null)));
        var commandRow = WinoLayout.HStack(8, _sendButton, _sendToServerButton, attach, _importance, WinoLayout.Spacer(), _draftStatus, _popOutButton, discard);
        commandRow.EdgeInsets = new NSEdgeInsets(10, 16, 10, 12);

        // ---- Header fields ----
        _fromPopup = new NSPopUpButton { PullsDown = false, Bordered = false, TranslatesAutoresizingMaskIntoConstraints = false };
        _fromPopup.Activated += (_, _) =>
        {
            int index = (int)_fromPopup.IndexOfSelectedItem;
            if (ViewModel.AvailableAliases is { } aliases && index >= 0 && index < aliases.Count) ViewModel.SelectedAlias = aliases[index];
        };
        _recipientDelegate = new RecipientDelegate(this);
        _toField = TokenField(Translator.ComposerTo.Trim().TrimEnd(':'));
        _ccField = TokenField("Cc");
        _bccField = TokenField("Bcc");
        var ccButton = TextButton("Cc", () => { ViewModel.IsCCBCCVisible = true; Window()?.MakeFirstResponder(_ccField); });
        var bccButton = TextButton("Bcc", () => { ViewModel.IsCCBCCVisible = true; Window()?.MakeFirstResponder(_bccField); });
        _ccBccButtons = WinoLayout.HStack(2, ccButton, bccButton);
        _subjectField = new NSTextField { Bordered = false, DrawsBackground = false, Font = WinoStyle.Body, FocusRingType = NSFocusRingType.None, TranslatesAutoresizingMaskIntoConstraints = false };
        _subjectField.PlaceholderString = Translator.ComposerSubject.Trim().TrimEnd(':');
        _subjectField.Changed += (_, _) => { ViewModel.Subject = _subjectField.StringValue; ScheduleAutosave(); };
        WinoAccessibility.Label(_subjectField, Translator.ComposerSubject.Trim().TrimEnd(':'));

        var fromRow = FieldRow(Translator.ComposerFrom.Trim().TrimEnd(':'), _fromPopup);
        var toRow = FieldRow(Translator.ComposerTo.Trim().TrimEnd(':'), _toField, _ccBccButtons);
        _ccRow = FieldRow("Cc", _ccField);
        _bccRow = FieldRow("Bcc", _bccField);
        var subjectRow = FieldRow(Translator.ComposerSubject.Trim().TrimEnd(':'), _subjectField);
        var fields = WinoLayout.VStack(0, fromRow, toRow, _ccRow, _bccRow, subjectRow);
        foreach (var row in fields.ArrangedSubviews) row.WidthAnchor.ConstraintEqualTo(fields.WidthAnchor).Active = true;

        // ---- Editor toolbar ----
        _toolbarTabs = NSSegmentedControl.FromLabels([Translator.EditorToolbarOption_Format, Translator.EditorToolbarOption_Insert, Translator.EditorToolbarOption_Options],
            NSSegmentSwitchTracking.SelectOne, ShowToolbarGroup);
        _toolbarTabs.ControlSize = NSControlSize.Small;
        _toolbarTabs.SelectedSegment = 0;
        _toolbarTabs.TranslatesAutoresizingMaskIntoConstraints = false;

        _fontPopup = new NSPopUpButton { PullsDown = false, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false };
        _fontPopup.AddItems(Fonts);
        _fontPopup.Activated += (_, _) => Run(EditorCommand.SetFontFamily(_fontPopup.TitleOfSelectedItem));
        WinoAccessibility.Label(_fontPopup, Translator.SettingsFontFamily_Title);
        _sizePopup = new NSPopUpButton { PullsDown = false, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false };
        _sizePopup.AddItems(FontSizes.Select(static size => size.ToString()).ToArray());
        _sizePopup.Activated += (_, _) => Run(EditorCommand.SetFontSize(FontSizes[Math.Max(0, (int)_sizePopup.IndexOfSelectedItem)]));
        WinoAccessibility.Label(_sizePopup, Translator.SettingsFontSize_Title);
        _boldButton = FormatButton(WinoIconGlyph.TextBold, Translator.Composer_Bold, EditorCommand.ToggleBold);
        _italicButton = FormatButton(WinoIconGlyph.TextItalic, Translator.Composer_Italic, EditorCommand.ToggleItalic);
        _underlineButton = FormatButton(WinoIconGlyph.TextUnderline, Translator.Composer_Underline, EditorCommand.ToggleUnderline);
        var bullets = FormatButton(WinoIconGlyph.TextBulletList, Translator.Composer_BulletList, EditorCommand.ToggleUnorderedList);
        var numbers = FormatButton(WinoIconGlyph.TextNumberList, Translator.Composer_OrderedList, EditorCommand.ToggleOrderedList);
        var outdent = FormatButton(WinoIconGlyph.TextIndentDecrease, Translator.Composer_Outdent, EditorCommand.Outdent);
        var indent = FormatButton(WinoIconGlyph.TextIndentIncrease, Translator.Composer_Indent, EditorCommand.Indent);
        var clear = FormatButton(WinoIconGlyph.TextClearFormatting, Translator.Composer_ClearFormatting, EditorCommand.ClearFormatting);
        var link = ToolbarButton(WinoIconGlyph.Link, Translator.Composer_InsertLink, () => Observe(InsertLinkAsync()));
        var image = FormatButton(WinoIconGlyph.Image, Translator.ComposerImagesDropZone_Message, EditorCommand.InsertImage);
        _formatGroup = WinoLayout.HStack(4, _fontPopup, _sizePopup, Divider(), _boldButton, _italicButton, _underlineButton, Divider(), bullets, numbers, outdent, indent, clear, Divider(), link, image);

        var table = ToolbarButton(WinoIconGlyph.Table, "Table", () => Run(EditorCommand.InsertTable(new EditorTableCommandArgs(3, 3))));
        var emoji = FormatButton(WinoIconGlyph.Emoji, "Emoji", EditorCommand.InsertEmoji);
        var insertLink = ToolbarButton(WinoIconGlyph.Link, Translator.Composer_InsertLink, () => Observe(InsertLinkAsync()), showTitle: true);
        var insertImage = ToolbarButton(WinoIconGlyph.Image, Translator.ComposerImagesDropZone_Message, () => Run(EditorCommand.InsertImage()), showTitle: false);
        _insertGroup = WinoLayout.HStack(4, insertLink, insertImage, table, emoji);
        _insertGroup.Hidden = true;

        var theme = ToolbarButton(WinoIconGlyph.DarkEditor, $"{Translator.Composer_LightTheme} / {Translator.Composer_DarkTheme}", () =>
        {
            _darkEditor = !_darkEditor;
            Run(EditorCommand.ToggleTheme(_darkEditor));
        }, showTitle: false);
        var spell = ToolbarButton(WinoIconGlyph.TextProofingTools, Translator.SettingsComposer_Title, () =>
            Run(EditorCommand.ToggleSpellCheck(!(_editor?.CurrentState.IsSpellCheckEnabled ?? true))));
        var undo = FormatButton(WinoIconGlyph.ArrowUndo, "Undo", EditorCommand.Undo);
        var redo = FormatButton(WinoIconGlyph.ArrowRedo, "Redo", EditorCommand.Redo);
        _optionsGroup = WinoLayout.HStack(4, theme, spell, Divider(), undo, redo);
        _optionsGroup.Hidden = true;

        var editorToolbar = WinoLayout.HStack(10, _toolbarTabs, _formatGroup, _insertGroup, _optionsGroup);
        editorToolbar.EdgeInsets = new NSEdgeInsets(8, 16, 8, 16);
        editorToolbar.SetClippingResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);

        // ---- Editor ----
        _webView = new WKWebView(CGRect.Empty, new WKWebViewConfiguration()) { TranslatesAutoresizingMaskIntoConstraints = false };
        _webView.SetValueForKey(NSNumber.FromBoolean(false), new NSString("drawsBackground"));
        WinoAccessibility.Label(_webView, Translator.Reader_MessageBodyAutomationName);
        var editorHost = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(_webView, editorHost, 6, 12, 0, 12);
        editorHost.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);

        // ---- Attachment tray ----
        _attachmentTray = WinoLayout.HStack(10);
        var trayScroll = new NSScrollView { DocumentView = _attachmentTray, HasHorizontalScroller = true, AutohidesScrollers = true, DrawsBackground = false, TranslatesAutoresizingMaskIntoConstraints = false };
        trayScroll.HeightAnchor.ConstraintEqualTo(54).Active = true;
        _attachmentTray.TopAnchor.ConstraintEqualTo(trayScroll.ContentView.TopAnchor).Active = true;
        _attachmentTray.LeadingAnchor.ConstraintEqualTo(trayScroll.ContentView.LeadingAnchor).Active = true;
        var traySeparator = new WinoSeparator();
        var trayRow = WinoLayout.HStack(10, trayScroll);
        trayRow.EdgeInsets = new NSEdgeInsets(10, 16, 12, 16);
        _attachmentHost = WinoLayout.VStack(0, traySeparator, trayRow);
        traySeparator.WidthAnchor.ConstraintEqualTo(_attachmentHost.WidthAnchor).Active = true;
        trayRow.WidthAnchor.ConstraintEqualTo(_attachmentHost.WidthAnchor).Active = true;
        trayScroll.WidthAnchor.ConstraintEqualTo(trayRow.WidthAnchor, 1, -32).Active = true;
        _attachmentHost.Hidden = true;

        var stack = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Spacing = 0,
            Alignment = NSLayoutAttribute.Leading,
            Distribution = NSStackViewDistribution.Fill,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        foreach (var view in new NSView[] { commandRow, new WinoSeparator(), fields, new WinoSeparator(), editorToolbar, new WinoSeparator(), editorHost, _attachmentHost })
        {
            stack.AddArrangedSubview(view);
            view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        }
        WinoLayout.Fill(stack, root);
        View = root;
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

    private NSTokenField TokenField(string label)
    {
        var field = new NSTokenField
        {
            Bordered = false,
            DrawsBackground = false,
            Font = WinoStyle.Body,
            FocusRingType = NSFocusRingType.None,
            TokenStyle = NSTokenStyle.Rounded,
            CompletionDelay = 0.15,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        field.Delegate = _recipientDelegate;
        field.CharacterSet = NSCharacterSet.FromString(",;");
        WinoAccessibility.Label(field, label);
        return field;
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

    private NSButton FormatButton(WinoIconGlyph glyph, string label, Func<EditorCommand> command)
        => ToolbarButton(glyph, label, () => Run(command()));

    private static NSButton TextButton(string title, Action action)
    {
        var button = new NSButton { Title = title, Bordered = false, BezelStyle = NSBezelStyle.Inline, Font = NSFont.SystemFontOfSize(12), ContentTintColor = WinoStyle.SecondaryText, TranslatesAutoresizingMaskIntoConstraints = false };
        button.Activated += (_, _) => action();
        return button;
    }

    private static NSView Divider()
    {
        var divider = new WinoSeparator(vertical: true);
        divider.HeightAnchor.ConstraintEqualTo(16).Active = true;
        return divider;
    }

    private void ShowToolbarGroup()
    {
        var index = _toolbarTabs.SelectedSegment;
        _formatGroup.Hidden = index != 0;
        _insertGroup.Hidden = index != 1;
        _optionsGroup.Hidden = index != 2;
    }

    // ---- Lifecycle ----

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        _editor = new AppKitHtmlMailEditorSession(_webView);
        _editor.Configure(_launcher);
        _editor.OperationFailed += (_, exception) => ReportError(exception);
        _editor.ContentChanged += (_, _) => ScheduleAutosave();
        _editor.StateChanged += (_, state) => _ = Dispatcher.ExecuteOnUIThread(() => ApplyEditorState(state));
        _editor.ImageInsertionRequested += (_, _) => _ = Dispatcher.ExecuteOnUIThread(() => Observe(PickImagesAsync()));
        _editor.ShortcutRequested += (_, kind) => { if (kind == EditorShortcutKind.OpenLinkDialog) _ = Dispatcher.ExecuteOnUIThread(() => Observe(InsertLinkAsync())); };
        _darkEditor = View.EffectiveAppearance.FindBestMatch([NSAppearance.NameAqua.ToString(), NSAppearance.NameDarkAqua.ToString()]) == NSAppearance.NameDarkAqua.ToString();

        ViewModel.GetHTMLBodyFunction = GetHtmlBodyAsync;
        ViewModel.RenderHtmlBodyAsyncFunc = RenderBodyAsync;
        ViewModel.CloseRequested += CloseRequested;
#if DEBUG
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
        Bind(nameof(ViewModel.IsDraftBusy), vm => vm.IsDraftBusy, _ => UpdateSendButtons());
        Bind(nameof(ViewModel.SelectedMessageImportance), vm => vm.SelectedMessageImportance, importance =>
            _importance.SelectItem(importance switch { MessageImportance.High => 0, MessageImportance.Low => 2, _ => 1 }));
        Bindings.Own(new CommandBinding(ViewModel.SendCommand, () => null, enabled => _sendButton.Enabled = enabled, Dispatcher, ReportError));
        ObserveRecipients(ViewModel.ToItems, _toField);
        ObserveRecipients(ViewModel.CCItems, _ccField);
        ObserveRecipients(ViewModel.BCCItems, _bccField);
        NotifyCollectionChangedEventHandler attachments = (_, _) => _ = Dispatcher.ExecuteOnUIThread(UpdateAttachments);
        ViewModel.IncludedAttachments.CollectionChanged += attachments;
        Bindings.Own(new ActionDisposable(() => ViewModel.IncludedAttachments.CollectionChanged -= attachments));
        EventHandler accentChanged = (_, _) => _sendButton.BezelColor = WinoStyle.Accent;
        WinoStyle.AccentChanged += accentChanged;
        Bindings.Own(new ActionDisposable(() => WinoStyle.AccentChanged -= accentChanged));
        UpdatePopOutButton();

        await _editor.InitializeAsync();
        await _editor.SetThemeAsync(_darkEditor);
        await ViewModel.InitializeNavigationAsync(mode, parameter!);
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (ViewModel.ToItems.Count == 0) Window()?.MakeFirstResponder(_toField);
        });
    }

    protected override async Task DeactivateAsync()
    {
        _autosave?.Cancel();
        ViewModel.CloseRequested -= CloseRequested;
        ViewModel.RewriteSession.Reset();
        ViewModel.RenderHtmlBodyAsyncFunc = null;
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

    private async Task RenderBodyAsync(string html)
    {
        if (_editor is null || _editorDisposed) return;
        await _editor.SetDefaultTypographyAsync(ViewModel.PreferencesService.ComposerFont, ViewModel.PreferencesService.ComposerFontSize);
        await _editor.Session.ExecuteCommandAsync(EditorCommand.ToggleSpellCheck(ViewModel.PreferencesService.IsComposerSpellCheckEnabled));
        await _editor.RenderHtmlAsync(html ?? string.Empty);
    }

    private void ApplyEditorState(EditorState state)
    {
        _boldButton.ContentTintColor = state.IsBold ? WinoStyle.Accent : WinoStyle.SecondaryText;
        _italicButton.ContentTintColor = state.IsItalic ? WinoStyle.Accent : WinoStyle.SecondaryText;
        _underlineButton.ContentTintColor = state.IsUnderline ? WinoStyle.Accent : WinoStyle.SecondaryText;
        if (!string.IsNullOrWhiteSpace(state.FontFamily))
        {
            var family = state.FontFamily.Split(',')[0].Trim().Trim('"', '\'');
            int index = Array.FindIndex(Fonts, font => string.Equals(font, family, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _fontPopup.SelectItem(index);
        }
        if (state.FontSize is { } size && int.TryParse(new string(size.ToString()!.TakeWhile(char.IsDigit).ToArray()), out var points))
        {
            int index = Array.IndexOf(FontSizes, points);
            if (index >= 0) _sizePopup.SelectItem(index);
        }
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

    private void UpdatePopOutButton()
    {
        if (_popOutButton is null) return;
        bool docked = _window is null;
        _popOutButton.Image = WinoIcons.Image(docked ? WinoIconGlyph.OpenInNewWindow : WinoIconGlyph.PanelLeft, 14, null, Translator.Buttons_PopOut);
        _popOutButton.Hidden = !docked && (_lastHost is null || !_lastHost.IsAvailable);
        if (docked && _lastHost is null) _popOutButton.Hidden = true;
    }

    private void UpdateAttachments()
    {
        foreach (var view in _attachmentTray.ArrangedSubviews)
        {
            _attachmentTray.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
            view.Dispose();
        }
        foreach (var attachment in ViewModel.IncludedAttachments.ToArray())
        {
            var name = WinoStyle.Label(attachment.FileName, NSFont.SystemFontOfSize(12, NSFontWeight.Semibold));
            name.WidthAnchor.ConstraintLessThanOrEqualTo(180).Active = true;
            var size = WinoStyle.Label(attachment.ReadableSize, WinoStyle.Caption, WinoStyle.SecondaryText);
            var remove = ToolbarButton(WinoIconGlyph.Dismiss, Translator.Buttons_Delete, () => ViewModel.RemoveAttachmentCommand.Execute(attachment));
            var icon = new WinoIconView(WinoIconGlyph.Document, 18, WinoStyle.SecondaryText);
            var row = WinoLayout.HStack(8, icon, WinoLayout.VStack(1, name, size), remove);
            row.EdgeInsets = new NSEdgeInsets(6, 8, 6, 6);
            var tile = new WinoSurfaceView { Fill = NSColor.ControlBackground, Stroke = NSColor.Separator, CornerRadius = WinoStyle.GroupRadius };
            WinoLayout.Fill(row, tile);
            tile.ToolTip = attachment.FileName;
            tile.Menu = CreateAttachmentMenu(attachment);
            _attachmentTray.AddArrangedSubview(tile);
        }
        _attachmentHost.Hidden = ViewModel.IncludedAttachments.Count == 0;
    }

    // Mirrors the WinUI composer attachment menu: open, save, then remove.
    private NSMenu CreateAttachmentMenu(MailAttachmentViewModel attachment)
    {
        var menu = new NSMenu { AutoEnablesItems = false };
        menu.AddItem(new NSMenuItem(Translator.Buttons_Open, (_, _) => Observe(ViewModel.OpenAttachmentCommand.ExecuteAsync(attachment))) { Image = WinoIcons.Image(WinoIconGlyph.OpenInNewWindow, 14) });
        menu.AddItem(new NSMenuItem(Translator.Buttons_Save, (_, _) => Observe(ViewModel.SaveAttachmentCommand.ExecuteAsync(attachment))) { Image = WinoIcons.Image(WinoIconGlyph.Save, 14) });
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(new NSMenuItem(Translator.Buttons_Remove, (_, _) => ViewModel.RemoveAttachmentCommand.Execute(attachment)) { Image = WinoIcons.Image(WinoIconGlyph.Dismiss, 14) });
        return menu;
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
            // No translation key exists for the composer's save status.
            await Dispatcher.ExecuteOnUIThread(() => _draftStatus.StringValue = $"Draft saved {DateTime.Now.ToString("t", System.Globalization.CultureInfo.CurrentCulture)}");
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

    // ---- Link and image insertion ----

    private async Task InsertLinkAsync()
    {
        if (Window() is not { } window) return;
        var url = new NSTextField(new CGRect(0, 30, 300, 24)) { PlaceholderString = Translator.Composer_LinkUrlPlaceholder };
        var text = new NSTextField(new CGRect(0, 0, 300, 24)) { PlaceholderString = Translator.Composer_LinkTextPlaceholder, StringValue = _editor?.CurrentState.SelectedText ?? string.Empty };
        var accessory = new NSView(new CGRect(0, 0, 300, 54));
        accessory.AddSubview(url);
        accessory.AddSubview(text);
        var alert = new NSAlert { MessageText = Translator.Composer_InsertLink, AccessoryView = accessory };
        alert.AddButton(Translator.Composer_InsertLink);
        alert.AddButton(Translator.Buttons_Cancel);
        alert.Window.InitialFirstResponder = url;
        var response = await alert.BeginSheetAsync(window);
        if ((long)response != 1000 || string.IsNullOrWhiteSpace(url.StringValue)) return;
        var address = url.StringValue.Trim();
        if (!address.Contains("://", StringComparison.Ordinal) && !address.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) address = "https://" + address;
        Run(EditorCommand.InsertLink(new EditorLinkCommandArgs(address, string.IsNullOrWhiteSpace(text.StringValue) ? null : text.StringValue)));
    }

    private async Task PickImagesAsync()
    {
        if (_editor is null || _editorDisposed) return;
        var panel = NSOpenPanel.OpenPanel;
        panel.AllowsMultipleSelection = true;
        panel.CanChooseDirectories = false;
#pragma warning disable CA1422
        panel.AllowedFileTypes = ["png", "jpg", "jpeg", "gif", "webp", "heic", "bmp"];
#pragma warning restore CA1422
        if (panel.RunModal() != 1) return;
        var images = new List<EditorImageInfo>();
        foreach (var url in panel.Urls)
        {
            if (url.Path is not { } path) continue;
            var bytes = await File.ReadAllBytesAsync(path);
            var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            var mime = extension switch { "jpg" or "jpeg" => "image/jpeg", "gif" => "image/gif", "webp" => "image/webp", "heic" => "image/heic", "bmp" => "image/bmp", _ => "image/png" };
            images.Add(new EditorImageInfo($"data:{mime};base64,{Convert.ToBase64String(bytes)}", Path.GetFileName(path)));
        }
        if (images.Count > 0) await _editor.InsertImagesAsync(images);
    }

    // ---- Pop-out window ----

    private void TogglePopOut()
    {
        if (_window is null) Observe(PopOutAsync());
        else Observe(DockAsync());
    }

    private async Task PopOutAsync()
    {
        var host = _lastHost;
        if (host is null || !host.IsAvailable) return;
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

    // ---- Recipients ----

    private ObservableCollection<AccountContact> CollectionFor(NSTokenField field)
        => ReferenceEquals(field, _ccField) ? ViewModel.CCItems : ReferenceEquals(field, _bccField) ? ViewModel.BCCItems : ViewModel.ToItems;

    private void ObserveRecipients(ObservableCollection<AccountContact> collection, NSTokenField field)
    {
        NotifyCollectionChangedEventHandler handler = (_, _) => _ = Dispatcher.ExecuteOnUIThread(() => { if (!_syncingRecipients) WriteTokens(field, collection); });
        collection.CollectionChanged += handler;
        Bindings.Own(new ActionDisposable(() => collection.CollectionChanged -= handler));
        WriteTokens(field, collection);
    }

    private void WriteTokens(NSTokenField field, IEnumerable<AccountContact> contacts)
    {
        var addresses = new List<NSObject>();
        foreach (var contact in contacts)
        {
            if (string.IsNullOrWhiteSpace(contact.Address)) continue;
            if (!string.IsNullOrWhiteSpace(contact.Name)) _recipientNames[contact.Address] = contact.Name;
            addresses.Add(new NSString(contact.Address));
        }
        field.ObjectValue = NSArray.FromNSObjects(addresses.ToArray());
    }

    private static IReadOnlyList<string> ReadTokens(NSTokenField field)
    {
        if (field.ObjectValue is not NSArray array) return [];
        var result = new List<string>();
        for (nuint index = 0; index < array.Count; index++)
        {
            var value = array.GetItem<NSObject>(index)?.ToString();
            if (!string.IsNullOrWhiteSpace(value)) result.Add(value.Trim());
        }
        return result;
    }

    private async Task SyncAllRecipientsAsync()
    {
        string[] to = [], cc = [], bcc = [];
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            to = ReadTokens(_toField).ToArray();
            cc = ReadTokens(_ccField).ToArray();
            bcc = ReadTokens(_bccField).ToArray();
        });
        await SyncRecipientsAsync(_toField, to);
        await SyncRecipientsAsync(_ccField, cc);
        await SyncRecipientsAsync(_bccField, bcc);
    }

    /// <summary>Makes the ViewModel collection match the tokens: removals first, then resolved additions.</summary>
    private async Task SyncRecipientsAsync(NSTokenField field, IReadOnlyList<string> tokens)
    {
        var collection = CollectionFor(field);
        var addresses = new List<string>();
        var invalid = new List<string>();
        foreach (var token in tokens)
        {
            if (MailboxAddress.TryParse(token, out var mailbox) && mailbox.Address.Contains('@'))
            {
                addresses.Add(mailbox.Address);
                if (!string.IsNullOrWhiteSpace(mailbox.Name)) _recipientNames[mailbox.Address] = mailbox.Name;
            }
            else invalid.Add(token);
        }

        _syncingRecipients = true;
        try
        {
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                foreach (var contact in collection.ToArray())
                    if (!addresses.Contains(contact.Address?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase)) collection.Remove(contact);
            });
            foreach (var address in addresses.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (ComposePageViewModel.ContainsAddress(collection, address)) continue;
                var recipient = await ViewModel.GetAddressInformationAsync(address, collection);
                if (recipient is null) continue;
                if (string.IsNullOrWhiteSpace(recipient.Name) && _recipientNames.TryGetValue(address, out var name)) recipient.Name = name;
                await Dispatcher.ExecuteOnUIThread(() => ViewModel.TryAddRecipient(collection, recipient));
            }
        }
        finally { _syncingRecipients = false; }

        if (invalid.Count > 0)
        {
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                foreach (var token in invalid) ViewModel.NotifyInvalidEmail(token);
                WriteTokens(field, collection);
            });
        }
    }

    private string[] CompletionsFor(string substring)
    {
        var query = substring?.Trim() ?? string.Empty;
        if (query.Length >= 2 && !string.Equals(query, _suggestionQuery, StringComparison.OrdinalIgnoreCase))
        {
            _suggestionQuery = query;
            Observe(RefreshSuggestionsAsync(query));
        }
        return _suggestions
            .Where(suggestion => !string.IsNullOrWhiteSpace(suggestion.Address) &&
                ((suggestion.Name ?? string.Empty).Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                 suggestion.Address.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Select(static suggestion => string.IsNullOrWhiteSpace(suggestion.Name) ? suggestion.Address : $"{suggestion.Name} <{suggestion.Address}>")
            .Distinct()
            .Take(8)
            .ToArray();
    }

    private async Task RefreshSuggestionsAsync(string query)
    {
        var results = await ViewModel.RecipientSuggestionService.SuggestAsync(ViewModel.ComposingAccount?.Id, query, 8, includeLists: false);
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (!string.Equals(query, _suggestionQuery, StringComparison.OrdinalIgnoreCase)) return;
            _suggestions.Clear();
            _suggestions.AddRange(results);
            foreach (var suggestion in results)
                if (!string.IsNullOrWhiteSpace(suggestion.Address) && !string.IsNullOrWhiteSpace(suggestion.Name)) _recipientNames[suggestion.Address] = suggestion.Name;
        });
    }

    private string DisplayString(string address)
        => _recipientNames.TryGetValue(address, out var name) && !string.IsNullOrWhiteSpace(name) ? name : address;

    private sealed class RecipientDelegate(ComposePageViewController owner) : NSTokenFieldDelegate
    {
        public override string[] GetCompletionStrings(NSTokenField tokenField, string substring, nint tokenIndex, nint selectedIndex)
            => owner.CompletionsFor(substring);

        public override NSObject GetRepresentedObject(NSTokenField tokenField, string editingString)
        {
            var text = editingString?.Trim() ?? string.Empty;
            if (MailboxAddress.TryParse(text, out var mailbox) && mailbox.Address.Contains('@'))
            {
                if (!string.IsNullOrWhiteSpace(mailbox.Name)) owner._recipientNames[mailbox.Address] = mailbox.Name;
                return new NSString(mailbox.Address);
            }
            return new NSString(text);
        }

        public override string GetDisplayString(NSTokenField tokenField, NSObject representedObject)
            => owner.DisplayString(representedObject?.ToString() ?? string.Empty);

        public override string GetEditingString(NSTokenField tokenField, NSObject representedObject)
            => representedObject?.ToString() ?? string.Empty;

        public override NSArray ShouldAddObjects(NSTokenField tokenField, NSArray tokens, nuint index)
        {
            owner.ScheduleRecipientSync(tokenField);
            return tokens;
        }

        [Export("controlTextDidEndEditing:")]
        public void EditingEnded(NSNotification notification)
        {
            if (notification.Object is NSTokenField field) owner.ScheduleRecipientSync(field);
        }
    }

    private void ScheduleRecipientSync(NSTokenField field)
    {
        // Runs after AppKit commits the new token into the field's object value.
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            if (Bindings.IsDisposed) return;
            var tokens = ReadTokens(field);
            Observe(SyncRecipientsAsync(field, tokens));
            ScheduleAutosave();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _autosave?.Cancel();
            ViewModel.CloseRequested -= CloseRequested;
            if (!_editorDisposed && _editor is not null) _ = DisposeEditorAsync();
            foreach (var field in new[] { _toField, _ccField, _bccField })
                if (field is not null) field.Delegate = null!;
            _recipientDelegate?.Dispose();
            _recipientDelegate = null;
        }
        base.Dispose(disposing);
    }
}
