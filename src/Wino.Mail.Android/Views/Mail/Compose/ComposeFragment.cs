using TooltipCompat = AndroidX.AppCompat.Widget.TooltipCompat;
using Android.Content;
using Android.Views;
using Android.Views.InputMethods;
using Android.Webkit;
using Android.Widget;
using AndroidX.Activity;
using AndroidX.Core.View;
using CommunityToolkit.Mvvm.Messaging;
using Google.Android.Material.Button;
using Google.Android.Material.ProgressIndicator;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Editor;
using Wino.Editor.Android;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.ViewModels;
using Wino.Messaging.Client.Mails;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;
using PopupMenu = AndroidX.AppCompat.Widget.PopupMenu;

namespace Wino.Mail.Android.Views.Mail.Compose;

/// <summary>
/// The composer (canvas boards Compose and ComposeDark; Windows ComposePage, the iOS and Mac ComposePageViewController)
/// over the shared <see cref="ComposePageViewModel"/>. It is a rendering-frame page: full screen over the shell on
/// compact widths, in the reader pane on medium and expanded widths, like the Windows and iPad composers. The bar
/// carries Close, the title with the draft status, Attach, More and the filled Send button (Send to server for a local
/// draft whose upload failed). The zone holds the draft-upload failure bar, From (an alias menu when the account has
/// several), To / Cc / Bcc (Recipients partial), Subject, the attachment strip (Attachments partial), the body in the
/// shared editor document (<see cref="AndroidHtmlMailEditorSession"/>) and the format bar (Format partial) right above
/// the keyboard. The draft is saved three seconds after the last change, whenever the page pauses (backgrounded, rotated,
/// covered) and when it closes; Close and Back save and close, Discard (in More) asks first.
/// </summary>
public sealed partial class ComposeFragment : WinoFragment<ComposePageViewModel>
{
    private IServiceProvider? _services;
    private IDispatcher? _dispatcher;
    private IMailDialogService? _dialogs;
    private ComposeBridge? _bridge;
    private AndroidHtmlMailEditorSession? _editor;
    private WebView? _web;
    private TextView? _title;
    private TextView? _status;
    private CircularProgressIndicator? _saving;
    private MaterialButton? _send;
    private MaterialButton? _more;
    private TextView? _from;
    private WinoContactPicture? _fromAvatar;
    private EditText? _subject;
    private WinoInfoBar? _syncFailed;
    private CancellationTokenSource? _autosave;
    private bool _compact;
    private bool _darkEditor;
    private bool _editorDisposed;
    private bool _sendEnabled;
    private bool _sendToServerEnabled;
    private bool _closing;

    protected override int LayoutResource => Resource.Layout.fragment_compose;

    /// <summary>Route hook: the body bridge is in place before the ViewModel loads the draft.</summary>
    public static void Prepare(PageEntry entry) => ComposeBridge.For(entry);

    protected override void OnBind(View view, ComposePageViewModel viewModel, IDispatcher dispatcher)
    {
        var services = _services = MainApplication.Services ?? throw new InvalidOperationException("The application services are not ready.");
        var context = view.RequireContext();
        _dispatcher = dispatcher;
        _dialogs = services.GetService<IMailDialogService>();
        _compact = services.GetRequiredService<IWindowSizeClassService>().WidthClass == WindowWidthClass.Compact;
        _darkEditor = WinoColors.IsDark(context);
        _editorDisposed = false;

        BuildInsets(view);
        BuildBar(view, context, viewModel);
        BuildFrom(view, context, viewModel);
        BuildRecipients(view, context, viewModel, dispatcher);
        BuildSubject(view, context, viewModel, dispatcher);
        BuildAttachments(view, context, viewModel, dispatcher);
        BuildEditor(view, context, viewModel, services);
        BuildFormatBar(view, context);
        BuildBack();
        BindState(viewModel, dispatcher);

        _bridge = Entry is { } entry ? ComposeBridge.For(entry) : null;
        _bridge?.Attach(this);
        Bindings.Own(new ActionDisposable(() =>
        {
            _autosave?.Cancel();
            _bridge?.Detach(this);
            CloseSuggestions();
            _ = DisposeEditorAsync();
        }));
    }

    public override void OnPause()
    {
        // "No background without a saved draft": the body goes back to the bridge before the editor can go away.
        if (Entry is { IsReleased: false } && !_closing) _bridge?.Pause(this);
        base.OnPause();
    }

    // ---- Insets and back ----

    private void BuildInsets(View view)
    {
        var root = view.Require<View>(Resource.Id.compose_root);
        var room = new KeyboardRoom(this, root);
        root.AddOnLayoutChangeListener(room);
        var observer = root.ViewTreeObserver;
        observer?.AddOnGlobalFocusChangeListener(room);
        Bindings.Own(new ActionDisposable(() =>
        {
            root.RemoveOnLayoutChangeListener(room);
            if (observer?.IsAlive == true) observer.RemoveOnGlobalFocusChangeListener(room);
        }));
        if (_compact)
        {
            // Full screen over the shell: clear the status bar, and the navigation bar or the keyboard, whichever is taller.
            WinoInsets.ApplyWithKeyboard(root, top: true);
            return;
        }
        // In the reader pane the shell already clears the system bars; only the keyboard is added.
        ViewCompat.SetOnApplyWindowInsetsListener(root, new KeyboardInsets());
        ViewCompat.RequestApplyInsets(root);
    }

    private sealed class KeyboardInsets : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(View view, WindowInsetsCompat insets)
        {
            var ime = insets.GetInsets(WindowInsetsCompat.Type.Ime()).Bottom;
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars()).Bottom;
            view.SetPadding(0, 0, 0, Math.Max(0, ime - bars));
            return insets;
        }
    }

    /// <summary>
    /// Landscape phones and short windows: with the keyboard open the composer may have less than
    /// <c>wino_compose_keyboard_tight_height</c> left. Then the field being typed in keeps the room: while the body
    /// has focus the bar and the header step aside so the body and the format bar stay above the keyboard; while a header
    /// field has focus the format bar steps aside. Everything returns when the keyboard closes.
    /// </summary>
    private sealed class KeyboardRoom(ComposeFragment owner, View root) : Java.Lang.Object, View.IOnLayoutChangeListener, ViewTreeObserver.IOnGlobalFocusChangeListener
    {
        private bool _bodyHadFocus;

        // Moving between the body and a header field keeps the keyboard (and the layout) as it is. The window can drop
        // focus while the keyboard opens and stays attached to the body, so the last focused field decides.
        public void OnGlobalFocusChanged(View? oldFocus, View? newFocus)
        {
            if (newFocus is null) return;
            _bodyHadFocus = owner._web is { } web && (ReferenceEquals(newFocus, web) || web.HasFocus);
            OnLayoutChange(root, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        private bool? _bodyTight;
        private bool? _headerTight;

        public void OnLayoutChange(View? view, int left, int top, int right, int bottom, int oldLeft, int oldTop, int oldRight, int oldBottom)
        {
            bool keyboard = ViewCompat.GetRootWindowInsets(root)?.IsVisible(WindowInsetsCompat.Type.Ime()) == true;
            int room = root.Height - root.PaddingTop - root.PaddingBottom;
            bool tight = keyboard && root.Context is { } context && room < WinoTokens.Px(context, Resource.Dimension.wino_compose_keyboard_tight_height);
            bool bodyFocused = owner._web?.HasFocus == true || (root.RootView?.FindFocus() is null && _bodyHadFocus);
            bool bodyTight = tight && bodyFocused;
            bool headerTight = tight && !bodyFocused;
            if (bodyTight == _bodyTight && headerTight == _headerTight) return;
            _bodyTight = bodyTight;
            _headerTight = headerTight;
            // Visibility changes request a new layout; post them out of this layout pass.
            root.Post(() =>
            {
                SetVisible(Resource.Id.compose_bar, !bodyTight);
                SetVisible(Resource.Id.compose_header_scroll, !bodyTight);
                SetVisible(Resource.Id.compose_format_scroll, !headerTight);
            });
        }

        private void SetVisible(int id, bool visible)
        {
            if (root.FindViewById(id) is { } child) child.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
        }
    }

    /// <summary>Back saves the draft and closes, like Close (the Gmail and Windows behaviour; nothing is lost).</summary>
    private void BuildBack()
    {
        if (Activity is not { } activity) return;
        var callback = new BackCallback(this);
        activity.OnBackPressedDispatcher.AddCallback(ViewLifecycleOwner, callback);
    }

    private sealed class BackCallback(ComposeFragment owner) : OnBackPressedCallback(true)
    {
        public override void HandleOnBackPressed()
        {
            if (owner.CloseSuggestionsIfOpen()) return;
            owner.Observe(owner.SaveAndCloseAsync());
        }
    }

    // ---- Bar ----

    private void BuildBar(View view, Context context, ComposePageViewModel viewModel)
    {
        _title = view.FindViewById<TextView>(Resource.Id.compose_title);
        _status = view.FindViewById<TextView>(Resource.Id.compose_status);
        _saving = view.FindViewById<CircularProgressIndicator>(Resource.Id.compose_saving);
        var primary = WinoColors.PrimaryText(context);
        var secondary = WinoColors.SecondaryText(context);

        // Material 3 full-screen dialog bar (canvas Material pass Compose): Close, title, Attach, Send, overflow.
        var close = view.Require<MaterialButton>(Resource.Id.compose_close);
        close.SetIconResource(Resource.Drawable.wino_ic_close);
        close.IconTint = global::Android.Content.Res.ColorStateList.ValueOf(primary);
        close.ContentDescription = Translator.Buttons_Close;
        OnClick(close, () => Observe(SaveAndCloseAsync()));

        var attach = view.Require<MaterialButton>(Resource.Id.compose_attach);
        attach.Icon = WinoIcons.Drawable(context, WinoIconGlyph.Attachment, ComposeStyle.Dp(context, Resource.Dimension.wino_compose_glyph_bar), secondary);
        attach.IconTint = null;
        attach.ContentDescription = Translator.Composer_AttachFiles;
        TooltipCompat.SetTooltipText(attach, Translator.Composer_AttachFiles);
        OnClick(attach, () => Observe(viewModel.AttachFilesCommand.ExecuteAsync(null)));

        _more = view.Require<MaterialButton>(Resource.Id.compose_more);
        _more.SetIconResource(Resource.Drawable.wino_ic_more_vert);
        _more.IconTint = global::Android.Content.Res.ColorStateList.ValueOf(secondary);
        _more.ContentDescription = Translator.More;
        TooltipCompat.SetTooltipText(_more, Translator.More);
        OnClick(_more, ShowMore);

        _send = view.Require<MaterialButton>(Resource.Id.compose_send);
        _send.Icon = WinoIcons.Drawable(context, WinoIconGlyph.Send, ComposeStyle.Dp(context, Resource.Dimension.wino_compose_glyph_send), WinoColors.AccentText(context));
        _send.IconTint = null;
        OnClick(_send, () => Observe(SendOrUploadAsync()));

        _syncFailed = view.FindViewById<WinoInfoBar>(Resource.Id.compose_sync_failed);
        if (_syncFailed is not null)
        {
            _syncFailed.Severity = WinoInfoBarSeverity.Warning;
            _syncFailed.Title = Translator.Draft_SyncFailedInfoBarTitle;
            _syncFailed.ActionTitle = Translator.Draft_RetryUpload;
            _syncFailed.IsClosable = false;
            // TalkBack reads the bar when it opens (the iOS announcement).
            _syncFailed.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
            EventHandler retry = (_, _) =>
            {
                if (viewModel.SendToServerCommand.CanExecute(null)) Observe(viewModel.SendToServerCommand.ExecuteAsync(null));
            };
            _syncFailed.ActionInvoked += retry;
            Bindings.Own(new ActionDisposable(() => _syncFailed.ActionInvoked -= retry));
        }
    }

    private bool IsUpload => ViewModel is { } viewModel && viewModel.ShouldShowSendToServerButton && !viewModel.ShouldShowSendButton;

    /// <summary>Send for a synchronized draft; Send to server for a local draft that could not be uploaded.</summary>
    private void UpdateSendButton()
    {
        if (_send is null) return;
        var upload = IsUpload;
        TooltipCompat.SetTooltipText(_send, upload ? Translator.Buttons_SendToServer : Translator.Buttons_Send);
        _send.ContentDescription = upload ? $"{Translator.Buttons_SendToServer}. {Translator.Composer_LocalDraftSyncInfo}" : Translator.Buttons_Send;
        _send.Enabled = upload ? _sendToServerEnabled : _sendEnabled;
        if (_send.Context is { } context) _send.Alpha = _send.Enabled ? 1f : ComposeStyle.Float(context, Resource.Dimension.wino_compose_disabled_alpha);
    }

    private void UpdateTitle()
    {
        if (_title is null || ViewModel is not { } viewModel) return;
        _title.Text = string.IsNullOrWhiteSpace(viewModel.Subject) ? Translator.MenuNewMail : viewModel.Subject;
    }

    private void UpdateDraftStatus()
    {
        if (_status is null || ViewModel is not { } viewModel) return;
        // A failed upload stays "busy" in the ViewModel until it retries; the failure bar says so, the spinner does not.
        var busy = viewModel.IsDraftBusy && !viewModel.IsDraftSyncFailed;
        if (_saving is not null) _saving.Visibility = busy ? ViewStates.Visible : ViewStates.Gone;
        // The Material 3 bar has no subtitle line: the status shows only while a save runs.
        _status.Text = busy ? Translator.Composer_SavingDraft : string.Empty;
        _status.Visibility = string.IsNullOrEmpty(_status.Text) ? ViewStates.Gone : ViewStates.Visible;
    }

    /// <summary>Windows DraftSyncFailedInfoBar: open while the upload failed, with the server's error.</summary>
    private void UpdateSyncFailedBar()
    {
        if (_syncFailed is null || ViewModel is not { } viewModel) return;
        _syncFailed.Message = viewModel.DraftSyncErrorMessage;
        var visible = viewModel.IsDraftSyncFailed ? ViewStates.Visible : ViewStates.Gone;
        if (_syncFailed.Visibility == visible) return;
        _syncFailed.Visibility = visible;
    }

    private void BindState(ComposePageViewModel viewModel, IDispatcher dispatcher)
    {
        Bind(viewModel, dispatcher, nameof(viewModel.Subject), vm => vm.Subject, subject =>
        {
            if (_subject is not null && (_subject.Text ?? string.Empty) != (subject ?? string.Empty)) _subject.Text = subject ?? string.Empty;
            UpdateTitle();
        });
        Bind(viewModel, dispatcher, nameof(viewModel.IsCCBCCVisible), vm => vm.IsCCBCCVisible, ApplyCcBccVisibility);
        Bind(viewModel, dispatcher, nameof(viewModel.AvailableAliases), vm => vm.AvailableAliases, _ => UpdateFrom());
        Bind(viewModel, dispatcher, nameof(viewModel.SelectedAlias), vm => vm.SelectedAlias, _ => UpdateFrom());
        Bind(viewModel, dispatcher, nameof(viewModel.ComposingAccount), vm => vm.ComposingAccount, _ => UpdateFrom());
        Bind(viewModel, dispatcher, nameof(viewModel.CurrentMailDraftItem), vm => vm.CurrentMailDraftItem, _ => UpdateSendButton());
        Bind(viewModel, dispatcher, nameof(viewModel.IsDraftBusy), vm => vm.IsDraftBusy, _ => { UpdateSendButton(); UpdateDraftStatus(); });
        Bind(viewModel, dispatcher, nameof(viewModel.IsDraftSyncFailed), vm => vm.IsDraftSyncFailed, _ => { UpdateSyncFailedBar(); UpdateDraftStatus(); });
        Bind(viewModel, dispatcher, nameof(viewModel.DraftSyncErrorMessage), vm => vm.DraftSyncErrorMessage, _ => UpdateSyncFailedBar());
        Bindings.Own(new CommandBinding(viewModel.SendCommand, () => null, enabled => { _sendEnabled = enabled; UpdateSendButton(); }, dispatcher, ReportError));
        Bindings.Own(new CommandBinding(viewModel.SendToServerCommand, () => null, enabled => { _sendToServerEnabled = enabled; UpdateSendButton(); }, dispatcher, ReportError));
    }

    // ---- From ----

    private void BuildFrom(View view, Context context, ComposePageViewModel viewModel)
    {
        view.Require<TextView>(Resource.Id.compose_from_caption).Text = FieldCaption(Translator.ComposerFrom);
        _fromAvatar = view.FindViewById<WinoContactPicture>(Resource.Id.compose_from_avatar);
        if (_fromAvatar is not null) _fromAvatar.Size = ComposeStyle.Dp(context, Resource.Dimension.wino_compose_from_avatar);
        _from = view.Require<TextView>(Resource.Id.compose_from);
        EventHandler pick = (_, _) => ShowAliasMenu();
        _from.Click += pick;
        Bindings.Own(new ActionDisposable(() => _from.Click -= pick));
    }

    internal static string FieldCaption(string text) => text.Trim().TrimEnd(':');

    /// <summary>The From row: the selected alias (a menu when the account has several), otherwise the account address.</summary>
    private void UpdateFrom()
    {
        if (_from is null || ViewModel is not { } viewModel || _from.Context is not { } context) return;
        var account = viewModel.ComposingAccount;
        var aliases = viewModel.AvailableAliases;
        string title;
        string? address;
        if (aliases is { Count: > 0 })
        {
            var selected = viewModel.SelectedAlias ?? aliases[0];
            title = AliasTitle(selected, account?.SenderName);
            address = selected.AliasAddress;
        }
        else
        {
            address = account?.Address;
            title = account is null ? string.Empty
                : string.IsNullOrWhiteSpace(account.SenderName) ? account.Address ?? string.Empty : $"{account.SenderName} <{account.Address}>";
        }
        var choosable = aliases is { Count: > 1 };
        _from.Text = title;
        _from.Clickable = choosable;
        _from.ContentDescription = $"{FieldCaption(Translator.ComposerFrom)}, {title}";
        _from.SetCompoundDrawablesRelativeWithIntrinsicBounds(null, null,
            choosable ? WinoIcons.Drawable(context, WinoIconGlyph.ChevronDown, ComposeStyle.Dp(context, Resource.Dimension.wino_compose_glyph_chevron), WinoColors.SecondaryText(context)) : null, null);
        _fromAvatar?.SetIdentity(account?.SenderName ?? address, address);
    }

    private static string AliasTitle(MailAccountAlias alias, string? senderName)
    {
        var name = string.IsNullOrWhiteSpace(alias.AliasSenderName) ? senderName : alias.AliasSenderName;
        return string.IsNullOrWhiteSpace(name) ? alias.AliasAddress ?? string.Empty : $"{name} <{alias.AliasAddress}>";
    }

    private void ShowAliasMenu()
    {
        if (_from?.Context is not { } context || ViewModel is not { } viewModel || viewModel.AvailableAliases is not { Count: > 1 } aliases) return;
        var popup = new PopupMenu(context, _from);
        var selected = viewModel.SelectedAlias ?? aliases[0];
        for (int index = 0; index < aliases.Count; index++)
        {
            var item = popup.Menu.Add(0, index, index, AliasTitle(aliases[index], viewModel.ComposingAccount?.SenderName));
            item?.SetCheckable(true);
            item?.SetChecked(ReferenceEquals(aliases[index], selected));
        }
        popup.Menu.SetGroupCheckable(0, true, true);
        popup.MenuItemClick += (_, args) =>
        {
            if (args.Item?.ItemId is int index && index >= 0 && index < aliases.Count)
            {
                viewModel.SelectedAlias = aliases[index];
                ScheduleAutosave();
            }
        };
        popup.Show();
    }

    // ---- Subject ----

    private void BuildSubject(View view, Context context, ComposePageViewModel viewModel, IDispatcher dispatcher)
    {
        view.Require<TextView>(Resource.Id.compose_subject_caption).Text = FieldCaption(Translator.ComposerSubject);
        _subject = view.Require<EditText>(Resource.Id.compose_subject);
        _subject.ContentDescription = FieldCaption(Translator.ComposerSubject);
        _subject.Text = viewModel.Subject ?? string.Empty;
        EventHandler<global::Android.Text.TextChangedEventArgs> changed = (_, _) =>
        {
            var text = _subject.Text ?? string.Empty;
            if (text == (viewModel.Subject ?? string.Empty)) return;
            viewModel.Subject = text;
            ScheduleAutosave();
        };
        _subject.TextChanged += changed;
        // Next in the subject moves into the message body (Windows SubjectTextBoxPreviewKeyDown).
        EventHandler<TextView.EditorActionEventArgs> action = (_, args) =>
        {
            args.Handled = args.ActionId is ImeAction.Next or ImeAction.Done;
            if (args.Handled) FocusBody();
        };
        _subject.EditorAction += action;
        Bindings.Own(new ActionDisposable(() =>
        {
            _subject.TextChanged -= changed;
            _subject.EditorAction -= action;
        }));
    }

    // ---- Editor ----

    private void BuildEditor(View view, Context context, ComposePageViewModel viewModel, IServiceProvider services)
    {
        _web = view.Require<WebView>(Resource.Id.compose_web);
        _web.ContentDescription = Translator.Reader_MessageBodyAutomationName;
        var editor = _editor = new AndroidHtmlMailEditorSession(_web);
        if (services.GetService<IExternalLauncher>() is { } launcher) editor.Configure(launcher);
        editor.OperationFailed += (_, exception) => ReportError(exception);
        editor.ContentChanged += (_, _) => _dispatcher?.ExecuteOnUIThread(ScheduleAutosave);
        editor.StateChanged += (_, state) => _dispatcher?.ExecuteOnUIThread(() => UpdateFormatState(state));
        editor.ShortcutRequested += (_, kind) =>
        {
            if (kind == EditorShortcutKind.OpenLinkDialog) _dispatcher?.ExecuteOnUIThread(ShowLinkDialog);
        };
        editor.ImageInsertionRequested += (_, _) => _dispatcher?.ExecuteOnUIThread(() => Observe(InsertPicturesAsync()));
        if (context.ContentResolver is { } resolver) editor.AcceptReceivedImages(resolver);
        Observe(InitializeEditorAsync(editor));
    }

    private async Task InitializeEditorAsync(AndroidHtmlMailEditorSession editor)
    {
        await editor.SetThemeAsync(_darkEditor);
        await editor.InitializeAsync();
    }

    /// <summary>
    /// Windows RenderComposeHtmlAsync: spell check, its language and autocorrect, typography, then the body. The bridge
    /// calls it for the ViewModel's render and for a new view.
    /// </summary>
    internal async Task RenderBodyAsync(string html)
    {
        if (_editor is not { } editor || _editorDisposed || ViewModel is not { } viewModel) return;
        var preferences = viewModel.PreferencesService;
        await editor.SetThemeAsync(_darkEditor);
        await editor.SetDefaultTypographyAsync(preferences.ComposerFont, preferences.ComposerFontSize);
        await editor.ExecuteCommandAsync(EditorCommand.ToggleSpellCheck(preferences.IsComposerSpellCheckEnabled));
        if (!string.IsNullOrWhiteSpace(preferences.ComposerSpellCheckLanguageCode))
            await editor.ExecuteCommandAsync(EditorCommand.SetSpellCheckLanguage(preferences.ComposerSpellCheckLanguageCode));
        await editor.ExecuteCommandAsync(EditorCommand.ToggleAutoCorrect(preferences.IsComposerAutoCorrectEnabled));
        await editor.RenderHtmlAsync(html ?? string.Empty);
    }

    /// <summary>The editor's body, or null when this view has no live editor.</summary>
    internal async Task<string?> ReadEditorHtmlAsync()
    {
        if (_editor is not { } editor || _editorDisposed) return null;
        try { return await editor.GetHtmlBodyAsync(); }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException or OperationCanceledException) { return null; }
    }

    internal Task ApplySignatureAsync(string html, string? previousHtml)
        => _editor is { } editor && !_editorDisposed ? editor.SetSignatureAsync(html, previousHtml) : Task.CompletedTask;

    private void FocusBody()
    {
        if (_editor is not { } editor || _editorDisposed) return;
        _web?.RequestFocus();
        Observe(editor.FocusEditorAsync(true));
        if (_web is not null && _web.Context?.GetSystemService(Context.InputMethodService) is InputMethodManager input)
            input.ShowSoftInput(_web, ShowFlags.Implicit);
    }

    /// <summary>A reply (it has In-Reply-To) starts in the body; anything else starts in To (Windows ApplyInitialFocusAsync).</summary>
    internal Task ApplyInitialFocusAsync(bool requested)
    {
        if (_editorDisposed || ViewModel is not { } viewModel) return Task.CompletedTask;
        return _dispatcher?.ExecuteOnUIThread(() =>
        {
            if (requested && IsReply(viewModel)) FocusBody();
            else if (requested || (viewModel.ToItems.Count == 0 && viewModel.CurrentMailDraftItem is not null)) FocusRecipientInput(_toRow);
        }) ?? Task.CompletedTask;
    }

    private static bool IsReply(ComposePageViewModel viewModel)
    {
        var mime = viewModel.CurrentMimeMessage;
        var inReplyTo = mime?.InReplyTo;
        if (string.IsNullOrWhiteSpace(inReplyTo)) inReplyTo = viewModel.CurrentMailDraftItem?.MailCopy?.InReplyTo;
        if (string.IsNullOrWhiteSpace(inReplyTo) && mime is not null && mime.Headers.Contains(HeaderId.InReplyTo))
            inReplyTo = mime.Headers[HeaderId.InReplyTo];
        return !string.IsNullOrWhiteSpace(inReplyTo);
    }

    private async Task DisposeEditorAsync()
    {
        if (_editor is not { } editor || _editorDisposed) return;
        _editorDisposed = true;
        var web = _web;
        _editor = null;
        try { await editor.DisposeAsync(); }
        catch (Exception exception) { ReportError(exception); }
        web?.Destroy();
    }

    private void SetEditorTheme(bool dark)
    {
        _darkEditor = dark;
        if (_editor is { } editor && !_editorDisposed) Observe(editor.ExecuteCommandAsync(EditorCommand.ToggleTheme(dark)));
    }

    // ---- Saving, sending and closing ----

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
        if (token.IsCancellationRequested || _editorDisposed || _closing || ViewModel is not { } viewModel) return;
        await SyncAllRecipientsAsync();
        if (await viewModel.UpdateMimeChangesAsync())
            await (_dispatcher?.ExecuteOnUIThread(MarkSaved) ?? Task.CompletedTask);
    }

    private void MarkSaved() => UpdateDraftStatus();

    private async Task SendOrUploadAsync()
    {
        if (ViewModel is not { } viewModel) return;
        await SyncAllRecipientsAsync();
        if (IsUpload)
        {
            if (viewModel.SendToServerCommand.CanExecute(null)) await viewModel.SendToServerCommand.ExecuteAsync(null);
            return;
        }
        _autosave?.Cancel();
        if (viewModel.SendCommand.CanExecute(null)) await viewModel.SendCommand.ExecuteAsync(null);
    }

    /// <summary>Close and Back: commit typed recipients, save the draft, then close. A composer without a draft closes at once.</summary>
    private async Task SaveAndCloseAsync()
    {
        if (_closing || ViewModel is not { } viewModel) return;
        _closing = true;
        try
        {
            _autosave?.Cancel();
            CloseSuggestions();
            if (viewModel.CurrentMailDraftItem is not null && viewModel.ComposingAccount is not null)
            {
                await SyncAllRecipientsAsync();
                await viewModel.UpdateMimeChangesAsync();
            }
            await DismissAsync();
        }
        catch
        {
            _closing = false;
            throw;
        }
    }

    /// <summary>Discard (More): confirm with the consequence on the button, then delete the draft and close.</summary>
    private async Task DiscardAsync()
    {
        if (_closing || ViewModel is not { } viewModel || _dialogs is not { } dialogs) return;
        if (viewModel.CurrentMailDraftItem is null || viewModel.ComposingAccount is null)
        {
            await DismissAsync();
            return;
        }
        var confirmed = await dialogs.ShowWinoCustomMessageDialogAsync(Translator.DialogMessage_DiscardDraftConfirmationTitle,
            Translator.DialogMessage_DiscardDraftConfirmationMessage, Translator.Buttons_Discard, WinoCustomMessageDialogIcon.Warning,
            Translator.Buttons_Cancel);
        if (!confirmed || _closing) return;
        _closing = true;
        _autosave?.Cancel();
        if (_bridge is not null) _bridge.IsClosing = true;
        try
        {
            await viewModel.DiscardDraftAsync(false);
            await DismissAsync();
        }
        catch
        {
            _closing = false;
            if (_bridge is not null) _bridge.IsClosing = false;
            throw;
        }
    }

    private async Task DismissAsync()
    {
        if (_bridge is not null) _bridge.IsClosing = true;
        await (_dispatcher?.ExecuteOnUIThread(() =>
        {
            HideKeyboard();
            // The mail list clears its selection and closes the frame (as on the Mac and iPad).
            WeakReferenceMessenger.Default.Send(new DisposeRenderingFrameRequested());
        }) ?? Task.CompletedTask);
        // Without a mail list (another mode is showing) the frame is closed here; a second close finds nothing.
        if (_services?.GetService<AndroidNavigationService>() is { } navigation && Entry is { IsReleased: false })
            await navigation.CloseRenderingFrameAsync();
    }

    private void HideKeyboard()
    {
        if (View is { } view && view.Context?.GetSystemService(Context.InputMethodService) is InputMethodManager input)
            input.HideSoftInputFromWindow(view.WindowToken, HideSoftInputFlags.None);
    }

    // ---- More ----

    private const int MoreCcBcc = 1, MoreReadReceipt = 2, MoreTheme = 3, MoreDiscard = 4;
    private const int ImportanceHigh = 10, ImportanceNormal = 11, ImportanceLow = 12;
    private const int SignatureNone = 100, SignatureFirst = 101;

    /// <summary>Cc/Bcc, importance, read receipt, signature, editor theme and Discard (Windows and Mac overflow order).</summary>
    private void ShowMore()
    {
        if (_more?.Context is not { } context || ViewModel is not { } viewModel) return;
        var popup = new PopupMenu(context, _more);
        var menu = popup.Menu;
        var ccBcc = menu.Add(0, MoreCcBcc, 0, Translator.Composer_CcBcc);
        ccBcc?.SetCheckable(true);
        ccBcc?.SetChecked(viewModel.IsCCBCCVisible);

        var importance = menu.AddSubMenu(0, 0, 1, Translator.Composer_Importance);
        if (importance is not null)
        {
            var current = viewModel.IsImportanceSelected ? viewModel.SelectedMessageImportance : MessageImportance.Normal;
            importance.Add(1, ImportanceHigh, 0, Translator.Composer_HighImportance)?.SetChecked(current == MessageImportance.High);
            importance.Add(1, ImportanceNormal, 1, Translator.Composer_NormalImportance)?.SetChecked(current == MessageImportance.Normal);
            importance.Add(1, ImportanceLow, 2, Translator.Composer_LowImportance)?.SetChecked(current == MessageImportance.Low);
            importance.SetGroupCheckable(1, true, true);
        }

        var receipt = menu.Add(0, MoreReadReceipt, 2, Translator.Composer_RequestReadReceipt);
        receipt?.SetCheckable(true);
        receipt?.SetChecked(viewModel.IsReadReceiptRequested);

        var signatures = viewModel.AvailableSignatures.ToList();
        if (signatures.Count > 0)
        {
            var signature = menu.AddSubMenu(0, 0, 3, Translator.SettingsSignature_Title);
            if (signature is not null)
            {
                signature.Add(2, SignatureNone, 0, Translator.SettingsSignature_NoneSignatureName)?.SetChecked(viewModel.SelectedSignature is null);
                for (int index = 0; index < signatures.Count; index++)
                    signature.Add(2, SignatureFirst + index, index + 1, signatures[index].Name)?.SetChecked(ReferenceEquals(signatures[index], viewModel.SelectedSignature));
                signature.SetGroupCheckable(2, true, true);
            }
        }

        menu.Add(0, MoreTheme, 4, _darkEditor ? Translator.Composer_LightTheme : Translator.Composer_DarkTheme);
        menu.Add(0, MoreDiscard, 5, Translator.Buttons_Discard);

        popup.MenuItemClick += (_, args) =>
        {
            switch (args.Item?.ItemId)
            {
                case MoreCcBcc:
                    viewModel.IsCCBCCVisible = !viewModel.IsCCBCCVisible;
                    if (viewModel.IsCCBCCVisible) FocusRecipientInput(_ccRow);
                    break;
                case ImportanceHigh: SetImportance(viewModel, MessageImportance.High); break;
                case ImportanceNormal: SetImportance(viewModel, MessageImportance.Normal); break;
                case ImportanceLow: SetImportance(viewModel, MessageImportance.Low); break;
                case MoreReadReceipt:
                    viewModel.IsReadReceiptRequested = !viewModel.IsReadReceiptRequested;
                    ScheduleAutosave();
                    break;
                case SignatureNone:
                    Observe(viewModel.ApplySignatureCommand.ExecuteAsync(null));
                    ScheduleAutosave();
                    break;
                case int id when id >= SignatureFirst && id - SignatureFirst < signatures.Count:
                    Observe(viewModel.ApplySignatureCommand.ExecuteAsync(signatures[id - SignatureFirst]));
                    ScheduleAutosave();
                    break;
                case MoreTheme: SetEditorTheme(!_darkEditor); break;
                case MoreDiscard: Observe(DiscardAsync()); break;
            }
        };
        popup.Show();
    }

    private void SetImportance(ComposePageViewModel viewModel, MessageImportance importance)
    {
        viewModel.SelectedMessageImportance = importance;
        // Normal is the absence of an importance header, not a third value to write.
        viewModel.IsImportanceSelected = importance != MessageImportance.Normal;
        ScheduleAutosave();
    }

    // ---- Helpers ----

    internal void Observe(Task? task)
    {
        if (task is null) return;
        _ = ObserveAsync(task);
    }

    private async Task ObserveAsync(Task task)
    {
        try { await task; }
        // A view torn down mid-operation (rotation, closing) leaves its editor disposed; the next view continues.
        catch (Exception exception) when (_editorDisposed && exception is ObjectDisposedException or OperationCanceledException) { }
        catch (Exception exception) { ReportError(exception); }
    }
}
