using System.Collections.Specialized;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Webkit;
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
using Wino.Editor;
using Wino.Editor.Android;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Android.Views.Shell;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Mails;
using Wino.Presentation.Android;
using PopupMenu = AndroidX.AppCompat.Widget.PopupMenu;

namespace Wino.Mail.Android.Views.Mail;

/// <summary>
/// The message reader (canvas boards Reader, Landscape and Tablet; Windows MailRenderingPage, the iOS and Mac readers)
/// over the shared <see cref="MailRenderingPageViewModel"/>. The body is the shared reader document in a WebView
/// through <see cref="AndroidHtmlMailReaderSession"/> (Wino.Editor.Android): remote content by the message's policy,
/// links through the external launcher. Commands come from the shared <see cref="MailReaderCommandLayout"/>: on compact
/// widths the bottom app bar carries Archive, Delete, Move, Flag and Unread with Reply anchored and the rest in More; in
/// the reader pane the Windows command bar sits at the top. Attachments open through the attachment platform service
/// (ACTION_VIEW). The ViewModel renders through a <see cref="ReaderBridge"/> that lives with the page, so a rotation
/// recreates the WebView and renders the same message again.
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

    private AndroidHtmlMailReaderSession? _reader;
    private WebView? _web;
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
    private WinoInfoBar? _imageBanner;
    private View? _attachmentStrip;
    private LinearLayout? _attachments;
    private MaterialButton? _saveAll;
    private View? _loading;
    private View? _content;
    private global::Android.Animation.ObjectAnimator? _loadingPulse;
    private TextView? _linkTarget;
    private bool _lastInputWasTouch;
    private MailReaderCommandPlan? _plan;
    private bool _compact;
    private bool _dark;
    private bool _disposed;
    private int _measureGeneration;

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

    /// <summary>The reader content is hidden behind the pulsing skeleton until the message is rendered.</summary>
    private void ApplyLoading(bool ready)
    {
        if (_content is not null) _content.Visibility = ready ? ViewStates.Visible : ViewStates.Invisible;
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
            row.AddView(date, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginStart = WinoTokens.Px(row.RequireContext(), Resource.Dimension.wino_space_s) });
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
        _imageBanner = view.FindViewById<WinoInfoBar>(Resource.Id.reader_image_banner);
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
        _web = view.FindViewById<WebView>(Resource.Id.reader_web);
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
                    if (_bridge?.Html is { } html) Observe(RenderAsync(html));
                    break;
                case ThemeId:
                    _dark = !_dark;
                    viewModel.IsDarkWebviewRenderer = _dark;
                    if (_reader is not null) Observe(_reader.SetThemeAsync(_dark));
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
        if (_imageBanner is not null)
        {
            _imageBanner.Severity = WinoInfoBarSeverity.Warning;
            _imageBanner.Message = Translator.ImageRenderingDisabled;
            _imageBanner.ActionTitle = Translator.Buttons_EnableImageRendering;
            _imageBanner.IsClosable = true;
            _imageBanner.ActionInvoked += (_, _) => Observe(ViewModel?.ForceImageLoadingCommand.ExecuteAsync(null));
        }
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
        Bind(viewModel, dispatcher, nameof(MailRenderingPageViewModel.IsMailContentReady), vm => vm.IsMailContentReady, ready =>
        {
            ApplyLoading(ready);
        });
        Bind(viewModel, dispatcher, nameof(MailRenderingPageViewModel.CurrentRenderModel), vm => vm.CurrentRenderModel, _ =>
        {
            if (_imageBanner is not null) _imageBanner.Visibility = ViewModel?.IsImageRenderingDisabled == true ? ViewStates.Visible : ViewStates.Gone;
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
        foreach (var attachment in attachments) _attachments.AddView(CreateTile(context, attachment));
        if (_attachmentStrip is not null) _attachmentStrip.Visibility = attachments.Length == 0 ? ViewStates.Gone : ViewStates.Visible;
        if (_saveAll is not null) _saveAll.Visibility = attachments.Length < 2 ? ViewStates.Gone : ViewStates.Visible;
    }

    /// <summary>An attachment as a Material 3 assist chip (canvas Material pass Reader): the type-coloured attachment glyph, "name · size".</summary>
    private View CreateTile(Context context, MailAttachmentViewModel attachment)
    {
        var extension = Path.GetExtension(attachment.FileName ?? string.Empty).TrimStart('.').ToUpperInvariant();
        var chip = LayoutInflater.From(context)?.Inflate(Resource.Layout.reader_attachment_chip, _attachments, false) as Google.Android.Material.Chip.Chip
            ?? throw new InvalidOperationException("reader_attachment_chip.xml did not inflate to a Chip.");
        chip.Text = $"{attachment.FileName} · {attachment.ReadableSize}";
        chip.ChipIcon = WinoIcons.Drawable(context, WinoIconGlyph.Attachment, WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph_small), ExtensionColor(extension));
        chip.ContentDescription = $"{attachment.FileName}, {attachment.ReadableSize}";
        chip.Click += (_, _) => Observe(ViewModel?.OpenAttachmentCommand.ExecuteAsync(attachment));
        chip.LongClick += (_, _) =>
        {
            var popup = new PopupMenu(context, chip);
            popup.Menu.Add(0, 0, 0, Translator.Buttons_Open);
            popup.Menu.Add(0, 1, 1, Translator.Buttons_Save);
            popup.MenuItemClick += (_, args) =>
            {
                if (args.Item?.ItemId == 0) Observe(ViewModel?.OpenAttachmentCommand.ExecuteAsync(attachment));
                else Observe(ViewModel?.SaveAttachmentCommand.ExecuteAsync(attachment));
            };
            popup.Show();
        };
        return chip;
    }

    private static global::Android.Graphics.Color ExtensionColor(string extension) => extension switch
    {
        "DOC" or "DOCX" => WinoColors.Hex(0x2B579A),
        "XLS" or "XLSX" or "CSV" => WinoColors.Hex(0x217346),
        "PPT" or "PPTX" => WinoColors.Hex(0xB7472A),
        "PDF" => WinoColors.Hex(0xD13438),
        "ZIP" or "RAR" or "7Z" => WinoColors.Hex(0x795548),
        _ => WinoColors.Hex(0x6E6E73)
    };

    // ---- Body ----

    private void BuildBody(View view, MailRenderingPageViewModel viewModel)
    {
        if (_web is null) return;
        _web.ContentDescription = Translator.Reader_MessageBodyAutomationName;
        _web.VerticalScrollBarEnabled = false;
        _web.OverScrollMode = OverScrollMode.Never;
        var reader = _reader = new AndroidHtmlMailReaderSession(_web);
        reader.Configure(viewModel.ExternalLauncher);
        reader.OperationFailed += (_, exception) => ReportError(exception);
        reader.NavigationRequested += (_, _) => _web?.Post(HideLinkTarget);
        reader.LinkHovered += (_, args) => _web?.Post(() => ShowLinkTarget(args));
        // A finger tap also reports its link (compatibility mouse events and focus); only a mouse, a stylus or the
        // keyboard shows the address, as hovering does on Windows. The listeners observe and never consume input.
        _web.Touch += (_, args) =>
        {
            _lastInputWasTouch = args.Event?.GetToolType(0) == MotionEventToolType.Finger;
            args.Handled = false;
        };
        _web.Hover += (_, args) =>
        {
            _lastInputWasTouch = false;
            args.Handled = false;
        };
        _web.KeyPress += (_, args) =>
        {
            _lastInputWasTouch = false;
            args.Handled = false;
        };
        Observe(InitializeReaderAsync(reader));
    }

    /// <summary>Shows the hovered or focused link's address (Windows WinoMailRenderer link bar); a Safe Links address shows where it leads.</summary>
    private void ShowLinkTarget(RendererLinkHoverEventArgs args)
    {
        if (_linkTarget is null || _disposed) return;
        if (args.Uri is null || _lastInputWasTouch)
        {
            HideLinkTarget();
            return;
        }
        _linkTarget.Text = args.SafeLinkTarget is { } target
            ? Translator.Reader_ProtectedLinkFormat.Replace("{0}", target, StringComparison.Ordinal)
            : args.Uri;
        _linkTarget.Visibility = ViewStates.Visible;
    }

    private void HideLinkTarget()
    {
        if (_linkTarget is null || _linkTarget.Visibility == ViewStates.Gone) return;
        _linkTarget.Visibility = ViewStates.Gone;
        _linkTarget.Text = string.Empty;
    }

    private async Task InitializeReaderAsync(AndroidHtmlMailReaderSession reader)
    {
        await reader.SetThemeAsync(_dark);
        await reader.InitializeAsync();
    }

    /// <summary>Renders the message (the bridge calls this for the ViewModel and when a new view attaches).</summary>
    internal async Task RenderAsync(string html)
    {
        try { await RenderCoreAsync(html); }
        // Switching messages quickly tears this view's session down mid-render; the next view renders.
        catch (Exception exception) when (_disposed && exception is ObjectDisposedException or System.OperationCanceledException) { }
    }

    private async Task RenderCoreAsync(string html)
    {
        if (_reader is not { } reader || _disposed || ViewModel is not { } viewModel) return;
        HideLinkTarget();
        if (_web is not null) SetWebHeight(120);
        var options = viewModel.CurrentRenderModel?.MailRenderingOptions;
        var policy = (options?.LoadImages ?? true) ? RemoteContentPolicy.ImagesAndFontsAllowed : RemoteContentPolicy.Blocked;
        await reader.SetThemeAsync(_dark);
        await reader.RenderAsync(new HtmlMailReaderRequest(string.IsNullOrEmpty(html) ? " " : html, policy,
            viewModel.IsReaderViewEnabled ? HtmlMailRenderMode.Readability : HtmlMailRenderMode.Original, options?.RenderPlaintextLinks ?? true));
        await reader.SetAccessibilityContextAsync(new ReaderAccessibilityContext(
            string.IsNullOrWhiteSpace(viewModel.Subject) ? Translator.MailItemNoSubject : viewModel.Subject,
            string.IsNullOrWhiteSpace(viewModel.FromName) ? viewModel.FromAddress : $"{viewModel.FromName} <{viewModel.FromAddress}>",
            MailRowPresentation.FormatReaderDate(viewModel.CreationDate, viewModel.PreferencesService.MailTimeFormatPreference),
            Translator.Reader_MessageBodyAutomationName,
            Translator.Reader_PlainTextFallbackAutomationName,
            viewModel.CurrentRenderModel?.AccessibleText ?? string.Empty));
        _ = MeasureAsync(++_measureGeneration);
        Observe(IntelligenceHtmlRenderedAsync(html));
    }

    internal async Task ClearAsync()
    {
        if (_reader is not { } reader || _disposed) return;
        HideLinkTarget();
        IntelligenceContentCleared();
        try { await reader.ClearAsync(); }
        catch (Exception exception) when (_disposed && exception is ObjectDisposedException or System.OperationCanceledException) { }
    }

    /// <summary>
    /// The body is as tall as its document so the page scroll owns vertical scrolling; it is measured again while late
    /// images and fonts change the layout.
    /// </summary>
    private async Task MeasureAsync(int generation)
    {
        foreach (var delay in new[] { 0, 250, 750, 1500, 3000 })
        {
            if (delay > 0) await Task.Delay(delay);
            if (_disposed || generation != _measureGeneration || _reader is null) return;
            var height = await _reader.MeasureContentHeightAsync();
            if (height > 0) Entry?.Dispatcher.ExecuteOnUIThread(() => { if (!_disposed && generation == _measureGeneration) SetWebHeight(height); });
        }
    }

    private void SetWebHeight(double dp)
    {
        if (_web is null || _web.Context is not { } context || _web.LayoutParameters is not { } layout) return;
        int pixels = Math.Max(WinoTokens.Px(context, Resource.Dimension.wino_m3_reader_body_min_height), WinoUnits.Dp(context, dp)) + WinoTokens.Px(context, Resource.Dimension.wino_space_s);
        if (Math.Abs(layout.Height - pixels) < 2) return;
        layout.Height = pixels;
        _web.LayoutParameters = layout;
    }

    private async Task DisposeReaderAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var reader = _reader;
        var web = _web;
        _reader = null;
        try { if (reader is not null) await reader.DisposeAsync(); }
        catch (Exception exception) { ReportError(exception); }
        web?.Destroy();
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
    /// The ViewModel's render and close hooks for the page's lifetime: it keeps the last message HTML and hands it to the
    /// reader view that is on screen, so a view created later (rotation, returning to the pane) renders it too.
    /// </summary>
    internal sealed class ReaderBridge : IDisposable
    {
        public const string StateKey = "mail-reader";

        private readonly MailRenderingPageViewModel _viewModel;
        private MailReaderFragment? _view;

        public ReaderBridge(MailRenderingPageViewModel viewModel)
        {
            _viewModel = viewModel;
            viewModel.RenderHtmlAsyncFunc = RenderAsync;
            viewModel.ClearRenderedHtmlAsyncFunc = ClearAsync;
            viewModel.CloseRequested += CloseRequested;
        }

        public string? Html { get; private set; }

        public void Attach(MailReaderFragment view)
        {
            _view = view;
            view.UpdateCommands();
            if (Html is { } html) view.Observe(view.RenderAsync(html));
        }

        public void Detach(MailReaderFragment view)
        {
            if (ReferenceEquals(_view, view)) _view = null;
        }

        private Task RenderAsync(string html)
        {
            Html = html;
            return _view?.RenderAsync(html) ?? Task.CompletedTask;
        }

        private Task ClearAsync()
        {
            Html = null;
            return _view?.ClearAsync() ?? Task.CompletedTask;
        }

        private void CloseRequested(object? sender, EventArgs args) => WeakReferenceMessenger.Default.Send(new DisposeRenderingFrameRequested());

        public void Dispose()
        {
            _viewModel.CloseRequested -= CloseRequested;
            _viewModel.RenderHtmlAsyncFunc = static _ => Task.CompletedTask;
            _viewModel.ClearRenderedHtmlAsyncFunc = static () => Task.CompletedTask;
            _view = null;
        }
    }
}
