using System.Collections.Specialized;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.CoordinatorLayout.Widget;
using CommunityToolkit.Mvvm.Messaging;
using Google.Android.Material.AppBar;
using Google.Android.Material.BottomAppBar;
using Google.Android.Material.Button;
using Google.Android.Material.FloatingActionButton;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Menus;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Rendering;
using Wino.Services.Models.Reader;
using Wino.Renderer;
using Wino.Renderer.Android;
using Wino.Renderer.Policy;
using Wino.Renderer.Theme;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Android.Rendering;
using Wino.Mail.Android.Views.Shell;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using Wino.Core.Domain.Messaging.Client.Mails;
using PopupMenu = AndroidX.AppCompat.Widget.PopupMenu;

namespace Wino.Mail.Android.Views.Mail;

/// <summary>
/// The message reader (canvas boards Reader, Landscape and Tablet; Windows MailRenderingPage, the iOS and Mac readers)
/// over the shared <see cref="MailRenderingPageViewModel"/>. The body is drawn by Wino's own mail renderer: the shared
/// <see cref="EngineMailMessageReader"/> shows the message from its MIME in a <see cref="MailView"/>; remote content,
/// tracker stripping and links come from the engine, links open through the ViewModel's OpenLinkAsync. Commands come from the shared <see cref="MailReaderCommandLayout"/>: on compact
/// widths the bottom app bar carries Archive, Delete, Move, Flag and Unread with Reply anchored and the rest in More; in
/// the reader pane the Windows command bar sits at the top. Attachments open through the attachment platform service
/// (ACTION_VIEW). The ViewModel renders through a <see cref="ReaderBridge"/> that lives with the page, so a rotation
/// recreates the view and shows the same message again.
/// </summary>
public sealed partial class MailReaderFragment : WinoFragment<MailRenderingPageViewModel>
{
    private static bool IsReplyOperation(MailOperation operation)
        => operation is MailOperation.Reply or MailOperation.ReplyAll or MailOperation.Forward;

    /// <summary>The compact reply button: the first visible of Reply, Reply all and Forward in the user's order.</summary>
    private MailOperation? _fabOperation;
    private bool _pillIconsOnly;

    private const int TopBarMoreId = -1;
    private const int BottomBarSlots = 5;

    private readonly EngineReaderPresentation _presentation = new();
    private MailView? _mailView;
    private EngineMailMessageReader? _messageReader;
    private ReaderBridge? _bridge;
    private AndroidSenderPictureProvider? _pictures;
    private MaterialToolbar? _toolbar;
    private BottomAppBar? _bottomBar;
    private FloatingActionButton? _replyButton;
    private LinearLayout? _commands;
    private TextView? _subject;
    private TextView? _sender;
    private TextView? _senderAddress;
    private TextView? _date;
    private TextView? _recipients;
    private WinoContactPicture? _avatar;
    private LinearLayout? _chips;
    private View? _attachmentStrip;
    private LinearLayout? _attachments;
    private MaterialButton? _saveAll;
    private View? _loading;
    private View? _content;
    private global::Android.Animation.ObjectAnimator? _loadingPulse;
    private TextView? _linkTarget;
    private MailReaderCommandPlan? _plan;
    private bool _compact;
    private bool _dark;
    private bool _disposed;

    protected override int LayoutResource => Resource.Layout.fragment_mail_reader;

    private MailItemViewModel? CurrentItem => Entry?.Parameter as MailItemViewModel;

    /// <summary>Route hook: the render bridge lives with the page and is in place before the ViewModel loads the message.</summary>
    public static void Prepare(PageEntry entry) => BridgeFor(entry);

    /// <summary>The page's render bridge, created on first use (the route, or the first view, which can come first). Main thread.</summary>
    private static ReaderBridge? BridgeFor(PageEntry entry)
    {
        if (entry.State.TryGetValue(ReaderBridge.StateKey, out var existing)) return existing as ReaderBridge;
        if (entry.IsReleased || entry.ViewModel is not MailRenderingPageViewModel viewModel) return null;
        var bridge = new ReaderBridge(viewModel);
        entry.State[ReaderBridge.StateKey] = bridge;
        return bridge;
    }

    protected override void OnBind(View view, MailRenderingPageViewModel viewModel, IDispatcher dispatcher)
    {
        var services = MainApplication.Services ?? throw new InvalidOperationException("The application services are not ready.");
        var context = view.RequireContext();
        _compact = services.GetRequiredService<IWindowSizeClassService>().WidthClass == WindowWidthClass.Compact;
        _pictures = services.GetRequiredService<AndroidSenderPictureProvider>();
        _dark = WinoColors.IsDark(context);
        viewModel.IsDarkWebviewRenderer = _dark;

        FindViews(view);
        BuildChrome(view, context);
        BuildHeader(context, viewModel, dispatcher);
        BuildHeaderExtras(view, context, viewModel, dispatcher, services);
        BuildPrivacy(view, context, viewModel, dispatcher);
        BuildInvitation(view, context, viewModel, dispatcher);
        BuildBody(view, viewModel);

        _bridge = Entry is { } current ? BridgeFor(current) : null;
        _bridge?.Attach(this);
        Bindings.Own(new ActionDisposable(() =>
        {
            _bridge?.Detach(this);
            _ = DisposeReaderAsync();
            _loadingPulse?.Cancel();
            _loadingPulse = null;
        }));
    }

    /// <summary>
    /// The reader content is hidden behind the pulsing skeleton until the message is rendered. It stays laid out and
    /// drawn (transparent, not invisible): the renderer's load waits for the body's first frame.
    /// </summary>
    private void ApplyLoading(bool ready)
    {
        if (_content is not null)
        {
            _content.Visibility = ViewStates.Visible;
            _content.Alpha = ready ? 1 : 0;
        }
        if (_loading is null) return;
        _loading.Visibility = ready ? ViewStates.Gone : ViewStates.Visible;
        if (ready)
        {
            _loadingPulse?.Cancel();
            return;
        }
        if (_loadingPulse is { IsRunning: true }) return;
        _loadingPulse ??= global::Android.Animation.ObjectAnimator.OfFloat(_loading, "alpha", 0.18f, 0.08f);
        _loadingPulse.SetDuration(800);
        _loadingPulse.RepeatMode = global::Android.Animation.ValueAnimatorRepeatMode.Reverse;
        _loadingPulse.RepeatCount = global::Android.Animation.ValueAnimator.Infinite;
        _loadingPulse.Start();
    }

    /// <summary>
    /// The date sits beside the sender when the reader is wide enough; in a narrow reader (the tablet's reader pane) it
    /// moves under the address so the name keeps its width.
    /// </summary>
    private void PlaceDate(LinearLayout row, LinearLayout column, int width)
    {
        if (_date is not { } date || width <= 0) return;
        bool beside = width >= WinoTokens.Px(row.RequireContext(), Resource.Dimension.wino_reader_date_beside_min_width);
        var parent = date.Parent as ViewGroup;
        if (beside ? ReferenceEquals(parent, row) : ReferenceEquals(parent, column)) return;
        parent?.RemoveView(date);
        if (beside)
        {
            date.Gravity = GravityFlags.End;
            date.SetMaxWidth(WinoTokens.Px(row.RequireContext(), Resource.Dimension.wino_reader_date_max_width));
            // The privacy button stays at the row's end.
            int index = _privacyButton is not null && ReferenceEquals(_privacyButton.Parent, row) ? row.IndexOfChild(_privacyButton) : -1;
            row.AddView(date, index, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginStart = WinoTokens.Px(row.RequireContext(), Resource.Dimension.wino_space_s) });
        }
        else
        {
            date.Gravity = GravityFlags.Start;
            date.SetMaxWidth(int.MaxValue);
            column.AddView(date, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        }
    }

    private void FindViews(View view)
    {
        _toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.reader_toolbar);
        _bottomBar = view.FindViewById<BottomAppBar>(Resource.Id.reader_bottom_bar);
        _replyButton = view.FindViewById<FloatingActionButton>(Resource.Id.reader_reply);
        _commands = view.FindViewById<LinearLayout>(Resource.Id.reader_commands);
        _subject = view.FindViewById<TextView>(Resource.Id.reader_subject);
        _sender = view.FindViewById<TextView>(Resource.Id.reader_sender);
        _senderAddress = view.FindViewById<TextView>(Resource.Id.reader_sender_address);
        _date = view.FindViewById<TextView>(Resource.Id.reader_date);
        _recipients = view.FindViewById<TextView>(Resource.Id.reader_recipients);
        _avatar = view.FindViewById<WinoContactPicture>(Resource.Id.reader_avatar);
        _chips = view.FindViewById<LinearLayout>(Resource.Id.reader_chips);
        FindPrivacyViews(view);
        _attachmentStrip = view.FindViewById(Resource.Id.reader_attachment_strip);
        _attachments = view.FindViewById<LinearLayout>(Resource.Id.reader_attachments);
        if (view.FindViewById<LinearLayout>(Resource.Id.reader_sender_row) is { } senderRow
            && view.FindViewById<LinearLayout>(Resource.Id.reader_sender_column) is { } senderColumn)
            senderRow.LayoutChange += (_, args) =>
            {
                // Views move on the next frame, not from inside the layout pass.
                int width = args.Right - args.Left;
                senderRow.Post(() => PlaceDate(senderRow, senderColumn, width));
            };
        _saveAll = view.FindViewById<MaterialButton>(Resource.Id.reader_save_all);
        _loading = view.FindViewById(Resource.Id.reader_loading);
        _content = view.FindViewById(Resource.Id.reader_scroll);
        _linkTarget = view.FindViewById<TextView>(Resource.Id.reader_link_target);
        // The page scrolls the reader, not the document, so the document never reports this scroll itself.
        if (view.FindViewById<AndroidX.Core.Widget.NestedScrollView>(Resource.Id.reader_scroll) is { } scroll)
            scroll.ScrollChange += (_, _) => HideLinkTarget();
    }

    // ---- Chrome: compact app bar and bottom bar, or the pane's command bar ----

    private void BuildChrome(View view, Context context)
    {
        if (_compact)
        {
            // Full screen over the shell: the reader pads itself clear of the status bar; the bottom bar pads itself.
            WinoInsets.Apply(view.FindViewById(Resource.Id.reader_column) ?? view, top: true, bottom: false);
            if (view.FindViewById(Resource.Id.reader_zone)?.LayoutParameters is ViewGroup.MarginLayoutParams margins)
                margins.BottomMargin = WinoTokens.Px(context, Resource.Dimension.wino_m3_bottom_bar_clearance);
            if (_toolbar is not null)
            {
                // Material 3 small top app bar: Back, no title (the subject is the headline below), Mark unread, overflow.
                _toolbar.SetNavigationIcon(Resource.Drawable.wino_ic_arrow_back);
                _toolbar.NavigationContentDescription = Translator.Buttons_Back;
                _toolbar.NavigationClick += (_, _) => Activity?.OnBackPressedDispatcher.OnBackPressed();
                _toolbar.MenuItemClick += (_, args) =>
                {
                    if (args.Item is not { } item) return;
                    if (item.ItemId == TopBarMoreId) ShowMore(_toolbar, includeReplies: true);
                    else RunSlot((MailOperation)item.ItemId);
                };
            }
            if (_replyButton is not null)
            {
                _replyButton.SetImageDrawable(WinoIcons.Drawable(context, WinoIconGlyph.Reply, WinoTokens.Dp(context, Resource.Dimension.wino_m3_fab_glyph), WinoColors.OnPrimaryContainer(context)));
                WinoMaterialAccent.Tint(_replyButton);
                _replyButton.ContentDescription = Translator.MailOperation_Reply;
                _replyButton.Click += (_, _) => { if (_fabOperation is { } operation) RunSlot(operation); };
                _replyButton.LongClick += (_, args) =>
                {
                    args.Handled = true;
                    ShowReplyMenu(_replyButton);
                };
            }
            if (_bottomBar is not null) _bottomBar.MenuItemClick += (_, args) => { if (args.Item is { } item) RunSlot((MailOperation)item.ItemId); };
            return;
        }
        // In the reader pane the shell already clears the system bars.
        if (_toolbar is not null) _toolbar.Visibility = ViewStates.Gone;
        if (_bottomBar is not null) _bottomBar.Visibility = ViewStates.Gone;
        if (_replyButton is not null) _replyButton.Visibility = ViewStates.Gone;
        if (view.FindViewById(Resource.Id.reader_command_scroll) is { } pill)
        {
            pill.Visibility = ViewStates.Visible;
            var surface = new Google.Android.Material.Shape.MaterialShapeDrawable(Google.Android.Material.Shape.ShapeAppearanceModel.InvokeBuilder().SetAllCornerSizes(WinoUnits.Dp(context, 28)).Build())
            {
                FillColor = global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.ZoneFill(context))
            };
            surface.SetStroke(Math.Max(1, WinoUnits.Dp(context, 1)), WinoColors.ZoneStroke(context));
            pill.Background = surface;
            pill.ClipToOutline = true;
            // The end of a message scrolls clear of the pill.
            if (view.FindViewById(Resource.Id.reader_scroll) is { } content)
                content.SetPadding(content.PaddingLeft, content.PaddingTop, content.PaddingRight, WinoUnits.Dp(context, 88));
        }
    }

    /// <summary>Rebuilds the commands from the shared layout (Windows OperationCommandBar).</summary>
    private void UpdateCommands()
    {
        if (ViewModel is not { } viewModel || View?.Context is not { } context) return;
        _plan = MailReaderCommandLayout.BuildInUserOrder(viewModel.MenuItems, CurrentItem is not null, offerMove: true);
        if (_compact) UpdateBottomBar(context);
        else UpdateCommandBar(context);
    }

    /// <summary>The compact top app bar: Back and More only; the message actions live in the bottom bar (option A, 3 Nov 2026).</summary>
    private void UpdateTopBar(Context context)
    {
        if (_toolbar is null) return;
        var menu = _toolbar.Menu;
        menu.Clear();
        var more = menu.Add(0, TopBarMoreId, 0, Translator.More);
        more?.SetIcon(Resource.Drawable.wino_ic_more_vert);
        more?.Icon?.SetTint(WinoColors.SecondaryText(context));
        more?.SetShowAsAction(ShowAsAction.Always);
    }

    /// <summary>
    /// The bottom bar shows the visible non-reply actions in the Reader actions order (past five, the bar's own overflow);
    /// the FAB runs the first visible reply action and its long press offers the other two.
    /// </summary>
    private void UpdateBottomBar(Context context)
    {
        if (_bottomBar is null || _plan is null) return;
        UpdateTopBar(context);
        var menu = _bottomBar.Menu;
        menu.Clear();
        int order = 0;
        foreach (var slot in _plan.Primary)
        {
            if (slot is null || IsReplyOperation(slot.Operation)) continue;
            var item = menu.Add(0, (int)slot.Operation, order++, slot.Title);
            item?.SetIcon(WinoIcons.Drawable(context, MailOperationDisplay.GetGlyph(slot.Operation), WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph), WinoColors.SecondaryText(context)));
            // Five icons beside the FAB fit from 360 dp; IfRoom would fold the bar after two.
            item?.SetShowAsAction(order <= BottomBarSlots ? ShowAsAction.Always : ShowAsAction.Never);
            item?.SetEnabled(slot.IsEnabled);
        }
        if (_replyButton is null) return;
        var reply = _plan.Primary.FirstOrDefault(static slot => slot is not null && IsReplyOperation(slot.Operation));
        _fabOperation = reply?.Operation;
        if (reply is null)
        {
            _replyButton.Hide();
            return;
        }
        _replyButton.SetImageDrawable(WinoIcons.Drawable(context, MailOperationDisplay.GetGlyph(reply.Operation), WinoTokens.Dp(context, Resource.Dimension.wino_m3_fab_glyph), WinoColors.OnPrimaryContainer(context)));
        _replyButton.ContentDescription = reply.Title;
        AndroidX.AppCompat.Widget.TooltipCompat.SetTooltipText(_replyButton, reply.Title);
        _replyButton.Enabled = reply.IsEnabled;
        _replyButton.Show();
    }

    /// <summary>The reply actions other than the FAB's, visible or hidden, in the Reader actions order.</summary>
    private IEnumerable<MailOperationMenuItem> OtherReplyItems()
    {
        if (_plan is null) yield break;
        foreach (var slot in _plan.Primary)
            if (slot is { Item: { } item } && IsReplyOperation(slot.Operation) && slot.Operation != _fabOperation) yield return item;
        foreach (var item in _plan.Overflow)
            if (IsReplyOperation(item.Operation) && item.Operation != _fabOperation) yield return item;
    }

    private void ShowReplyMenu(View anchor)
    {
        if (Context is not { } context || ViewModel is not { } viewModel) return;
        var items = OtherReplyItems().ToArray();
        if (items.Length == 0) return;
        var popup = new PopupMenu(context, anchor);
        for (int index = 0; index < items.Length; index++)
            popup.Menu.Add(0, index, index, MailOperationDisplay.GetTitle(items[index].Operation))?.SetEnabled(items[index].IsEnabled);
        popup.MenuItemClick += (_, args) =>
        {
            if (args.Item?.ItemId is int index && index >= 0 && index < items.Length) Observe(viewModel.OperationClickedCommand.ExecuteAsync(items[index]));
        };
        popup.Show();
    }

    private void UpdateCommandBar(Context context)
    {
        if (_commands is null || _plan is null) return;
        _commands.RemoveAllViews();
        int size = WinoTokens.Px(context, Resource.Dimension.wino_m3_icon_button);
        double glyph = WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph_small);
        foreach (var slot in _plan.Primary)
        {
            if (slot is null)
            {
                _commands.AddView(new Space(context), new LinearLayout.LayoutParams(WinoTokens.Px(context, Resource.Dimension.wino_space_m), WinoTokens.Px(context, Resource.Dimension.wino_space_hairline)));
                continue;
            }
            bool labelled = !_pillIconsOnly && IsReplyOperation(slot.Operation) && ViewModel?.PreferencesService.IsShowActionLabelsEnabled != false;
            var button = labelled
                ? new MaterialButton(context, null, Resource.Attribute.borderlessButtonStyle) { Text = slot.Title }
                : new MaterialButton(context, null, Resource.Attribute.borderlessButtonStyle);
            button.Icon = WinoIcons.Drawable(context, MailOperationDisplay.GetGlyph(slot.Operation), glyph, WinoColors.PrimaryText(context));
            button.IconTint = global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.PrimaryText(context));
            button.SetTextColor(WinoColors.PrimaryText(context));
            button.ContentDescription = slot.Title;
            AndroidX.AppCompat.Widget.TooltipCompat.SetTooltipText(button, slot.Title);
            button.Enabled = slot.IsEnabled;
            if (!labelled)
            {
                button.IconPadding = 0;
                button.SetPadding(0, 0, 0, 0);
                button.IconGravity = MaterialButton.IconGravityTextStart;
                button.SetMinWidth(size);
                button.SetMinimumWidth(size);
            }
            var operation = slot.Operation;
            button.Click += (_, _) => RunSlot(operation);
            _commands.AddView(button, new LinearLayout.LayoutParams(labelled ? ViewGroup.LayoutParams.WrapContent : size, size));
        }
        var more = new MaterialButton(context, null, Resource.Attribute.borderlessButtonStyle) { ContentDescription = Translator.More };
        AndroidX.AppCompat.Widget.TooltipCompat.SetTooltipText(more, Translator.More);
        more.Icon = WinoIcons.Drawable(context, WinoIconGlyph.More, glyph, WinoColors.PrimaryText(context));
        more.IconTint = global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.PrimaryText(context));
        more.IconPadding = 0;
        more.SetPadding(0, 0, 0, 0);
        more.SetMinWidth(size);
        more.SetMinimumWidth(size);
        more.Click += (_, _) => ShowMore(more, includeReplies: false);
        _commands.AddView(more, new LinearLayout.LayoutParams(size, size));
        // A pill wider than the reader pane drops the reply labels, as the Windows command bar does.
        if (!_pillIconsOnly && _commands.Parent is View pill)
            _commands.Post(() =>
            {
                if (_pillIconsOnly || pill.Width <= 0 || _commands is null || _commands.Width <= pill.Width) return;
                _pillIconsOnly = true;
                UpdateCommandBar(context);
            });
    }

    /// <summary>Runs the primary command for <paramref name="operation"/>; Move opens the folder picker for the open message.</summary>
    private void RunSlot(MailOperation operation)
    {
        if (ViewModel is not { } viewModel || _plan?.Primary.FirstOrDefault(slot => slot?.Operation == operation) is not { Item: { } item }) return;
        if (operation == MailOperation.Move && TryShowMove()) return;
        Observe(viewModel.OperationClickedCommand.ExecuteAsync(item));
    }

    private bool TryShowMove()
    {
        if (CurrentItem?.MailCopy is not MailCopy mail) return false;
        var list = MainApplication.Services?.GetService<AndroidNavigationService>()?.ShellContentEntry?.ViewModel as MailListPageViewModel;
        return list is not null && MailMovePresenter.TryShow(this, list, [mail], _compact);
    }

    /// <summary>More: the remaining operations, then Reader View and the reader theme (Windows OperationCommandBar).</summary>
    private void ShowMore(View anchor, bool includeReplies)
    {
        if (Context is not { } context || ViewModel is not { } viewModel || _plan is null) return;
        var popup = new PopupMenu(context, anchor);
        var entries = new List<MailOperationMenuItem>();
        void Add(MailOperationMenuItem item)
        {
            var title = MailOperationDisplay.GetTitle(item.Operation);
            if (string.IsNullOrEmpty(title)) return;
            entries.Add(item);
            popup.Menu.Add(0, entries.Count - 1, entries.Count, title)?.SetEnabled(item.IsEnabled);
        }
        if (includeReplies)
            foreach (var slot in _plan.Primary)
                if (slot is { Item: { } item } && IsReplyOperation(slot.Operation) && slot.Operation != _fabOperation) Add(item);
        foreach (var item in _plan.Overflow)
            if (item.Operation != MailOperation.Seperator) Add(item);
        const int ReaderViewId = 1000;
        const int ThemeId = 1001;
        popup.Menu.Add(1, ReaderViewId, 1000, Translator.Reader_ReaderView)?.SetCheckable(true)?.SetChecked(viewModel.IsReaderViewEnabled);
        popup.Menu.Add(1, ThemeId, 1001, _dark ? Translator.Composer_LightTheme : Translator.Composer_DarkTheme);
        popup.MenuItemClick += (_, args) =>
        {
            switch (args.Item?.ItemId)
            {
                case ReaderViewId:
                    viewModel.IsReaderViewEnabled = !viewModel.IsReaderViewEnabled;
                    _bridge?.RenderAgain();
                    break;
                case ThemeId:
                    _dark = !_dark;
                    viewModel.IsDarkWebviewRenderer = _dark;
                    ApplyReaderPresentation();
                    break;
                case int index when index >= 0 && index < entries.Count:
                    if (entries[index].Operation == MailOperation.Move && TryShowMove()) break;
                    Observe(viewModel.OperationClickedCommand.ExecuteAsync(entries[index]));
                    break;
            }
        };
        popup.Show();
    }

    // ---- Header ----

    private void BuildHeader(Context context, MailRenderingPageViewModel viewModel, IDispatcher dispatcher)
    {
        if (_avatar is not null) _avatar.Size = 40;
        if (_saveAll is not null)
        {
            _saveAll.Text = Translator.Reader_SaveAllAttachmentButtonText;
            _saveAll.Icon = WinoIcons.Drawable(context, WinoIconGlyph.ArrowDownload, 16, WinoColors.AccentText(context));
            _saveAll.Click += (_, _) => Observe(ViewModel?.SaveAllAttachmentsCommand.ExecuteAsync(null));
        }

        Bind(viewModel, dispatcher, nameof(MailRenderingPageViewModel.Subject), vm => vm.Subject, subject =>
        {
            if (_subject is not null) _subject.Text = string.IsNullOrWhiteSpace(subject) ? Translator.MailItemNoSubject : subject;
        });
        Bind(viewModel, dispatcher, nameof(MailRenderingPageViewModel.FromName), vm => vm.FromName, _ => UpdateSender());
        Bind(viewModel, dispatcher, nameof(MailRenderingPageViewModel.FromAddress), vm => vm.FromAddress, _ => UpdateSender());
        Bind(viewModel, dispatcher, nameof(MailRenderingPageViewModel.CreationDate), vm => vm.CreationDate, _ => UpdateRecipients());
        Bind(viewModel, dispatcher, nameof(MailRenderingPageViewModel.MenuItems), vm => vm.MenuItems, _ => UpdateCommands());
        System.ComponentModel.PropertyChangedEventHandler preferenceChanged = (_, args) =>
        {
            if (args.PropertyName != nameof(IPreferencesService.IsShowActionLabelsEnabled)) return;
            _ = dispatcher.ExecuteOnUIThread(() =>
            {
                _pillIconsOnly = false;
                UpdateCommands();
            });
        };
        viewModel.PreferencesService.PropertyChanged += preferenceChanged;
        Bindings.Own(new ActionDisposable(() => viewModel.PreferencesService.PropertyChanged -= preferenceChanged));
        Bind(viewModel, dispatcher, nameof(MailRenderingPageViewModel.IsMailContentReady), vm => vm.IsMailContentReady, ready =>
        {
            ApplyLoading(ready);
        });
        Bind(viewModel, dispatcher, nameof(MailRenderingPageViewModel.CurrentRenderModel), vm => vm.CurrentRenderModel, _ =>
        {
            UpdatePrivacy();
            UpdateRecipients();
            UpdateAttachments();
        });
        ObserveCollection(viewModel.ToItems, attachments: false, dispatcher);
        ObserveCollection(viewModel.CcItems, attachments: false, dispatcher);
        ObserveCollection(viewModel.Attachments, attachments: true, dispatcher);
        EventHandler<string?> pictureChanged = (_, address) =>
        {
            if (address is null || string.Equals(ViewModel?.FromAddress?.Trim(), address, StringComparison.OrdinalIgnoreCase)) UpdateSender();
        };
        _pictures!.PictureChanged += pictureChanged;
        Bindings.Own(new ActionDisposable(() => _pictures.PictureChanged -= pictureChanged));
        UpdateChips(context);
    }

    private void ObserveCollection(INotifyCollectionChanged collection, bool attachments, IDispatcher dispatcher)
    {
        NotifyCollectionChangedEventHandler handler = (_, _) => _ = dispatcher.ExecuteOnUIThread(() =>
        {
            if (Bindings.IsDisposed) return;
            if (attachments) UpdateAttachments();
            else UpdateRecipients();
        });
        collection.CollectionChanged += handler;
        Bindings.Own(new ActionDisposable(() => collection.CollectionChanged -= handler));
    }

    private void UpdateSender()
    {
        if (ViewModel is not { } viewModel) return;
        var name = string.IsNullOrWhiteSpace(viewModel.FromName) ? viewModel.FromAddress : viewModel.FromName;
        if (_sender is not null) _sender.Text = name ?? string.Empty;
        if (_senderAddress is not null)
        {
            bool same = string.Equals(name, viewModel.FromAddress, StringComparison.OrdinalIgnoreCase);
            _senderAddress.Text = same ? string.Empty : viewModel.FromAddress ?? string.Empty;
            _senderAddress.Visibility = same || string.IsNullOrEmpty(viewModel.FromAddress) ? ViewStates.Gone : ViewStates.Visible;
        }
        if (_avatar is not null)
        {
            _avatar.SetIdentity(name, viewModel.FromAddress);
            _avatar.Image = _pictures?.Get(viewModel.FromAddress);
        }
    }

    private void UpdateRecipients()
    {
        if (ViewModel is not { } viewModel) return;
        if (_date is not null) _date.Text = MailRowPresentation.FormatReaderDate(viewModel.CreationDate, viewModel.PreferencesService.MailTimeFormatPreference);
        if (_recipients is null) return;
        var lines = new List<string>();
        if (viewModel.ToItems.Count > 0) lines.Add(Translator.ComposerTo + string.Join(", ", viewModel.ToItems.Select(static contact => contact.ShortNameOrYouLabel)));
        if (viewModel.CcItems.Count > 0) lines.Add("Cc: " + string.Join(", ", viewModel.CcItems.Select(static contact => contact.ShortNameOrYouLabel)));
        _recipients.Text = string.Join("\n", lines);
        _recipients.Visibility = lines.Count == 0 ? ViewStates.Gone : ViewStates.Visible;
    }

    /// <summary>Category chips under the sender (the intelligence tiles belong to the intelligence header, not built here).</summary>
    private void UpdateChips(Context context)
    {
        if (_chips is null) return;
        _chips.RemoveAllViews();
        if (CurrentItem is { } item)
        {
            foreach (var category in item.Categories.Take(3))
            {
                var chip = new WinoChipView(context) { Text = category.Name ?? string.Empty, MaxTextWidth = WinoTokens.Dp(context, Resource.Dimension.wino_size_field), ChipHeight = WinoTokens.Dp(context, Resource.Dimension.wino_size_swatch_small) };
                var fill = WinoColors.FromHexString(category.BackgroundColorHex) ?? WinoColors.SubtleFill(context);
                chip.Fill = fill;
                chip.TextColor = WinoColors.FromHexString(category.TextColorHex) ?? WinoColors.ReadableText(fill);
                var margins = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = WinoTokens.Px(context, Resource.Dimension.wino_space_xs) };
                _chips.AddView(chip, margins);
            }
        }
        _chips.Visibility = _chips.ChildCount == 0 ? ViewStates.Gone : ViewStates.Visible;
    }

    // ---- Attachments (open through IAttachmentPlatformService: ACTION_VIEW, no Magika) ----

    private void UpdateAttachments()
    {
        if (_attachments is null || ViewModel is not { } viewModel || View?.Context is not { } context) return;
        _attachments.RemoveAllViews();
        var attachments = viewModel.Attachments.ToArray();
        foreach (var attachment in attachments) _attachments.AddView(CreateTile(context, _attachments, attachment));
        if (_attachmentStrip is not null) _attachmentStrip.Visibility = attachments.Length == 0 ? ViewStates.Gone : ViewStates.Visible;
        if (_saveAll is not null) _saveAll.Visibility = attachments.Length < 2 ? ViewStates.Gone : ViewStates.Visible;
    }

    /// <summary>An attachment tile (AttachmentTiles): a tap opens the file, a long press offers Open and Save.</summary>
    private View CreateTile(Context context, ViewGroup parent, MailAttachmentViewModel attachment)
    {
        var tile = AttachmentTiles.Create(context, parent, attachment, open: () => Observe(ViewModel?.OpenAttachmentCommand.ExecuteAsync(attachment)));
        tile.LongClick += (_, _) =>
        {
            var popup = new PopupMenu(context, tile);
            popup.Menu.Add(0, 0, 0, Translator.Buttons_Open);
            popup.Menu.Add(0, 1, 1, Translator.Buttons_Save);
            popup.MenuItemClick += (_, args) =>
            {
                if (args.Item?.ItemId == 0) Observe(ViewModel?.OpenAttachmentCommand.ExecuteAsync(attachment));
                else Observe(ViewModel?.SaveAttachmentCommand.ExecuteAsync(attachment));
            };
            popup.Show();
        };
        return tile;
    }

    // ---- Body ----

    /// <summary>
    /// The body is the native mail renderer's view, created in code and as tall as its mail, so the page scroll owns
    /// vertical scrolling while the view takes pinches and horizontal pans of a wide or zoomed mail.
    /// </summary>
    private void BuildBody(View view, MailRenderingPageViewModel viewModel)
    {
        if (view.FindViewById<FrameLayout>(Resource.Id.reader_body_card) is not { } card || MainApplication.Services is not { } services) return;
        var context = view.RequireContext();
        var mailView = _mailView = new MailView(context)
        {
            AutoHeight = true,
            ContentDescription = Translator.Reader_MessageBodyAutomationName,
        };
        mailView.SetMinimumHeight(WinoTokens.Px(context, Resource.Dimension.wino_m3_reader_body_min_height));
        card.AddView(mailView, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        _messageReader = new EngineMailMessageReader(mailView, services.GetRequiredService<MailEngineHost>(), _presentation,
            viewModel.SecureMimeContextFactory, () => viewModel.ShouldStripLinkTrackingParameters);
        _messageReader.Attach();
        viewModel.PrintPresenter = new AndroidMailPrintPresenter(services.GetRequiredService<MailEngineHost>(), () => Activity,
            () => _messageReader?.HasMessage == true ? _mailView?.Reader : null, () => CreatePrintHeader(viewModel));
        mailView.LinkActivated += (_, args) => Observe(OpenLinkAsync(args));
        mailView.LinkHoverChanged += (_, args) => ShowLinkTarget(args.Link);
        mailView.ContextMenuRequested += (_, args) => ShowBodyMenu(mailView, args);
    }

    private static Wino.Renderer.Print.PrintHeader CreatePrintHeader(MailRenderingPageViewModel viewModel)
    {
        var sender = string.IsNullOrWhiteSpace(viewModel.FromName) ? viewModel.FromAddress
            : string.IsNullOrWhiteSpace(viewModel.FromAddress) ? viewModel.FromName : $"{viewModel.FromName} <{viewModel.FromAddress}>";
        static string Recipients(IEnumerable<AccountContactViewModel> recipients) => string.Join("; ", recipients.Select(recipient =>
            string.IsNullOrWhiteSpace(recipient.Name) || string.Equals(recipient.Name, recipient.Address, StringComparison.OrdinalIgnoreCase)
                ? recipient.Address
                : $"{recipient.Name} <{recipient.Address}>"));
        return new Wino.Renderer.Print.PrintHeader(string.IsNullOrWhiteSpace(viewModel.Subject) ? Translator.MailItemNoSubject : viewModel.Subject,
            sender, Recipients(viewModel.ToItems), Recipients(viewModel.CcItems),
            MailRowPresentation.FormatReaderDate(viewModel.CreationDate, viewModel.PreferencesService.MailTimeFormatPreference));
    }

    private void ApplyReaderPresentation()
    {
        if (ViewModel is not { } viewModel) return;
        _presentation.IsDark = _dark;
        _presentation.Method = viewModel.ReaderDarkModeMethod == MailDarkModeMethod.Inverted ? DarkModeMethod.Inverted : DarkModeMethod.Adaptive;
        _presentation.SetTypography(viewModel.PreferencesService.ReaderFont, viewModel.PreferencesService.ReaderFontSize);
        _presentation.TextScale = Context?.Resources?.Configuration?.FontScale ?? 1;
        if (_mailView is not null) _presentation.ApplyTo(_mailView);
    }

    /// <summary>Long press in the body: the word is selected; Copy, Select all, and for a link Open and Copy link.</summary>
    private void ShowBodyMenu(MailView view, ContextMenuRequest request)
    {
        if (Context is not { } context) return;
        var popup = new PopupMenu(context, view, (int)GravityFlags.NoGravity);
        const int CopyId = 1;
        const int SelectAllId = 2;
        const int CopyLinkId = 3;
        if (request.HasSelection) popup.Menu.Add(0, CopyId, 0, Translator.Buttons_Copy);
        popup.Menu.Add(0, SelectAllId, 1, Translator.Reader_ContextMenu_SelectAll);
        if (request.LinkUrl is { Length: > 0 }) popup.Menu.Add(0, CopyLinkId, 2, Translator.Reader_ContextMenu_CopyLink);
        popup.MenuItemClick += (_, args) =>
        {
            switch (args.Item?.ItemId)
            {
                case CopyId:
                    view.CopySelection();
                    break;
                case SelectAllId:
                    view.SelectAll();
                    break;
                case CopyLinkId when request.LinkUrl is { } url && context.GetSystemService(Context.ClipboardService) is ClipboardManager clipboard:
                    clipboard.PrimaryClip = ClipData.NewPlainText("link", url);
                    break;
            }
        };
        popup.Show();
    }

    /// <summary>The hovered link's address under a mouse or stylus (Windows link bar); a Safe Links address shows where it leads.</summary>
    private void ShowLinkTarget(ResolvedLink? link)
    {
        if (_linkTarget is null || _disposed) return;
        if (link is null || string.IsNullOrEmpty(link.Effective))
        {
            HideLinkTarget();
            return;
        }
        _linkTarget.Text = link.RedirectTarget is { } target
            ? Translator.Reader_ProtectedLinkFormat.Replace("{0}", target, StringComparison.Ordinal)
            : link.Effective;
        _linkTarget.Visibility = ViewStates.Visible;
    }

    private void HideLinkTarget()
    {
        if (_linkTarget is null || _linkTarget.Visibility == ViewStates.Gone) return;
        _linkTarget.Visibility = ViewStates.Gone;
        _linkTarget.Text = string.Empty;
    }

    /// <summary>Shows the message (the bridge calls this for the ViewModel and when a new view attaches).</summary>
    internal async Task<MailRenderModel> RenderAsync(MailMessageRenderRequest request, CancellationToken cancellationToken)
    {
        if (_messageReader is not { } reader || _disposed || ViewModel is not { } viewModel)
            throw new System.OperationCanceledException(cancellationToken);
        HideLinkTarget();
        ApplyReaderPresentation();
        reader.IsReaderView = viewModel.IsReaderViewEnabled;

        // Read before the load starts: the engine's worker owns the message until the load ends.
        string html = EngineMailMessageReader.GetBodyHtml(request.Message);
        UpdateAccessibleMailContext(viewModel, string.Empty);
        var model = await reader.RenderAsync(request, cancellationToken);
        UpdateAccessibleMailContext(viewModel, model.AccessibleText);
        if (html.Length == 0 && reader.GetLoadedBodyHtml() is { Length: > 0 } decrypted) html = decrypted;
        Observe(IntelligenceHtmlRenderedAsync(html));
        return model;
    }

    /// <summary>What a screen reader says for the body: subject, sender, date and the text when nothing could be drawn.</summary>
    private void UpdateAccessibleMailContext(MailRenderingPageViewModel viewModel, string accessibleText)
    {
        if (_mailView is null) return;
        _mailView.AccessibilityContext = new Wino.Renderer.Accessibility.AccessibilityContext
        {
            Subject = string.IsNullOrWhiteSpace(viewModel.Subject) ? Translator.MailItemNoSubject : viewModel.Subject,
            Sender = string.IsNullOrWhiteSpace(viewModel.FromName) ? viewModel.FromAddress : $"{viewModel.FromName} <{viewModel.FromAddress}>",
            Date = MailRowPresentation.FormatReaderDate(viewModel.CreationDate, viewModel.PreferencesService.MailTimeFormatPreference),
            BodyName = Translator.Reader_MessageBodyAutomationName,
            PlainTextFallbackName = Translator.Reader_PlainTextFallbackAutomationName,
            FallbackText = accessibleText,
        };
    }

    internal async Task ClearAsync()
    {
        if (_messageReader is not { } reader || _disposed) return;
        HideLinkTarget();
        IntelligenceContentCleared();
        await reader.ClearAsync();
    }

    private Task DisposeReaderAsync()
    {
        if (_disposed) return Task.CompletedTask;
        _disposed = true;
        _messageReader?.Detach();
        _messageReader = null;
        _mailView = null;
        return Task.CompletedTask;
    }

    private void Observe(Task? task)
    {
        if (task is null) return;
        _ = ObserveAsync(task);
    }

    private async Task ObserveAsync(Task task)
    {
        try { await task; }
        // A view torn down mid-render (rotation, closing the reader) leaves its session disposed; the next view renders.
        catch (Exception exception) when (_disposed && exception is ObjectDisposedException or System.OperationCanceledException) { }
        catch (Exception exception) { ReportError(exception); }
    }

    /// <summary>
    /// The ViewModel's message reader for the page's lifetime: it keeps the last message and hands it to the reader view
    /// that is on screen, so a view created later (rotation, returning to the pane) shows it too.
    /// </summary>
    internal sealed class ReaderBridge : IMailMessageReader, IDisposable
    {
        public const string StateKey = "mail-reader";

        private readonly MailRenderingPageViewModel _viewModel;
        private MailReaderFragment? _view;
        private TaskCompletionSource<MailReaderFragment>? _waiting;

        public ReaderBridge(MailRenderingPageViewModel viewModel)
        {
            _viewModel = viewModel;
            viewModel.MessageReader = this;
            viewModel.CloseRequested += CloseRequested;
        }

        public MailMessageRenderRequest? Request { get; private set; }

        public void Attach(MailReaderFragment view)
        {
            _view = view;
            view.UpdateCommands();
            _waiting?.TrySetResult(view);
            _waiting = null;
            if (Request is { } request) view.Observe(view.RenderAsync(request, CancellationToken.None));
        }

        public void Detach(MailReaderFragment view)
        {
            if (ReferenceEquals(_view, view)) _view = null;
        }

        /// <summary>The same message again with the view's current presentation (theme, reader view).</summary>
        public void RenderAgain()
        {
            if (_view is { } view && Request is { } request) view.Observe(view.RenderAsync(request, CancellationToken.None));
        }

        async Task<MailRenderModel> IMailMessageReader.RenderAsync(MailMessageRenderRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            var view = _view;
            if (view is null)
            {
                // The ViewModel can load before the page's first view is bound.
                _waiting ??= new TaskCompletionSource<MailReaderFragment>(TaskCreationOptions.RunContinuationsAsynchronously);
                view = await _waiting.Task.WaitAsync(cancellationToken);
            }

            return await view.RenderAsync(request, cancellationToken);
        }

        Task IMailMessageReader.ClearAsync()
        {
            Request = null;
            return _view?.ClearAsync() ?? Task.CompletedTask;
        }

        private void CloseRequested(object? sender, EventArgs args) => WeakReferenceMessenger.Default.Send(new DisposeRenderingFrameRequested());

        public void Dispose()
        {
            _viewModel.CloseRequested -= CloseRequested;
            if (ReferenceEquals(_viewModel.MessageReader, this)) _viewModel.MessageReader = null;
            _waiting?.TrySetCanceled();
            _view = null;
        }
    }
}
