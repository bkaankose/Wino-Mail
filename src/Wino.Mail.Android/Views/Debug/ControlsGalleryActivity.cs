#if DEBUG
using System.ComponentModel;
using Android.App;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using AndroidX.Core.Widget;
using AndroidX.RecyclerView.Widget;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Android.MailList;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.Controls.Android.Shell;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Core.AccountIcon;
using Wino.Mail.ViewModels.Collections;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Debug;

/// <summary>
/// Debug-only gallery of the Android Wino controls (controls/Wino.Presentation.Android and Wino.Mail.Controls.Android)
/// with deterministic sample data, for visual checks in light and dark. Not part of Release builds. Open it with
/// <c>adb shell am start -n com.winomail.android/com.winomail.android.ControlsGallery [--ez dark true]</c>.
/// </summary>
[Activity(Name = "com.winomail.android.ControlsGallery", Exported = true, Theme = "@style/Theme.Wino", Label = "Wino controls")]
public sealed class ControlsGalleryActivity : AppCompatActivity
{
    private MailListProjection? _projection;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Delegate.SetLocalNightMode(Intent?.GetBooleanExtra("dark", false) == true ? AppCompatDelegate.ModeNightYes : AppCompatDelegate.ModeNightNo);
        base.OnCreate(savedInstanceState);
        WindowCompat.SetDecorFitsSystemWindows(Window ?? throw new InvalidOperationException("The activity has no window."), false);

        var root = new FrameLayout(this);
        root.AddView(new WinoBackdropView(this), new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        if (Assets is { } assets)
        {
            using var stream = assets.Open(WinoColors.IsDark(this) ? "Themes/Nighty.jpg" : "Themes/Clouds.jpg");
            WinoBackdrop.Set(global::Android.Graphics.BitmapFactory.DecodeStream(stream));
        }

        var column = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.AddView(column, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        WinoInsets.Apply(column);

        // Bar over the wallpaper: blurred slice (API 31+) under the tint.
        var bar = new FrameLayout(this);
        bar.AddView(new WinoBarBackdropView(this), new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        var title = new TextView(this) { Text = "Wino controls" };
        title.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 20);
        title.SetTextColor(WinoColors.PrimaryText(this));
        title.SetPadding(WinoUnits.Dp(this, 16), WinoUnits.Dp(this, 14), 0, WinoUnits.Dp(this, 14));
        bar.AddView(title);
        column.AddView(bar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, WinoUnits.Dp(this, 56)));

        var zone = new WinoZoneLayout(this);
        var zoneLayout = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1);
        int gutter = WinoUnits.Dp(this, 8);
        zoneLayout.SetMargins(gutter, 0, gutter, gutter);
        column.AddView(zone, zoneLayout);

        var scroll = new NestedScrollView(this);
        zone.AddView(scroll);
        var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        content.SetPadding(0, WinoUnits.Dp(this, 4), 0, WinoUnits.Dp(this, 16));
        scroll.AddView(content);

        AddMailList(content);
        AddShellRows(content);
        AddCommon(content);
        AddSettings(content);
        SetContentView(root);
    }

    protected override void OnDestroy()
    {
        _projection?.Dispose();
        base.OnDestroy();
    }

    private void AddMailList(LinearLayout content)
    {
        var now = DateTimeOffset.Now;
        var items = new MailListCollection<SampleMail>();
        items.AddRange(
        [
            new SampleMail("Ayşe Demir", "ayse@example.com", "Q4 roadmap review", "Hi Burak, I pulled together the roadmap notes from Thursday…", now.AddMinutes(-20)) { IsUnread = true, HasAttachments = true },
            new SampleMail("GitHub", "notifications@github.com", "[wino-mail] Android shell PR ready", "bkaankose requested your review on #2355…", now.AddHours(-2)) { IsUnread = true, IsFlagged = true },
            new SampleMail("Stripe", "receipts@stripe.com", "A new tax invoice is available", "Your Sep 1 to Sep 30 tax invoice is ready to download.", now.AddHours(-4)) { Category = ("Finance", new global::Android.Graphics.Color(0x27, 0xAE, 0x60)) },
            new SampleMail("Haifang Wang", "haifang@example.com", "RE: Windows application issues", "Hi team, I wanted to gently circle back on the bug report…", now.AddDays(-1)),
            new SampleMail("mBank", "info@mbank.example", "Your statement", "The monthly statement for your account is attached.", now.AddDays(-1).AddHours(-3)) { HasAttachments = true, IsDraft = true }
        ]);
        _projection = new MailListProjection(items, new MailListProjectionOptions { IsThreadingEnabled = false });

        var adapter = new WinoMailListAdapter(Map, MailRowPresentation.GroupTitle);
        var list = new RecyclerView(this) { NestedScrollingEnabled = false };
        list.SetLayoutManager(new LinearLayoutManager(this));
        list.SetAdapter(adapter);
        adapter.Attach(_projection);
        adapter.RowClicked += (_, row) => adapter.SelectOnly(row);
        adapter.RowLongClicked += (_, row) => { adapter.IsSelectionMode = !adapter.IsSelectionMode; adapter.SetSelected(row, true); };
        WinoMailSwipeHelper.Attach(list, adapter, (_, leading) => leading ? MailOperation.Archive : MailOperation.SoftDelete, (_, _) => { });
        if (adapter.RowAt(2) is { } selected) adapter.SelectOnly(selected);
        content.AddView(list, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
    }

    private static WinoMailRowModel? Map(MailListRow row)
    {
        if (row.SourceItem is not SampleMail mail) return null;
        return new WinoMailRowModel
        {
            Kind = WinoMailRowKind.Single,
            Sender = mail.Sender,
            SenderAddress = mail.Address,
            Subject = mail.Subject,
            Preview = mail.Preview,
            DateText = MailRowPresentation.FormatListDate(mail.DateSortKey.UtcDateTime),
            IsUnread = mail.IsUnread,
            IsFlagged = mail.IsFlagged,
            IsDraft = mail.IsDraft,
            HasAttachments = mail.HasAttachments,
            Tiles = mail.Category is { } category
                ? new WinoMailRowTile[] { new(category.Name, null, category.Color, null, false), new("Action needed", WinoIcons.Glyph(WinoIconGlyph.Sparkle), null, null, true) }
                : Array.Empty<WinoMailRowTile>(),
            AccessibilityText = $"{mail.Sender}, {mail.Subject}"
        };
    }

    private void AddShellRows(LinearLayout content)
    {
        Section(content, "Shell rows");
        var newItem = new WinoShellRowView(this);
        newItem.ConfigureNewItem("New mail");
        var account = new WinoShellRowView(this) { ShowsIndicator = true };
        account.ConfigureAccount(new AccountIconInfo(AccountIconProvider.Microsoft, null, "#2C3E50"), "Burak Kaan Köse", "bkaankose@outlook.com", 12);
        account.SetSync(true);
        var inbox = new WinoShellRowView(this) { IsRowSelected = true };
        inbox.ConfigureFolder(WinoIconGlyph.SpecialFolderInbox, "Inbox", 12);
        var drafts = new WinoShellRowView(this);
        drafts.ConfigureFolder(WinoIconGlyph.SpecialFolderDraft, "Drafts", 2, subtleCount: true);
        var header = new WinoShellRowView(this);
        header.ConfigureHeader("More");
        var more = new WinoShellRowView(this);
        more.ConfigureFolder(WinoIconGlyph.Folder, "Projects", 0, depth: 1, isExpanded: false);
        foreach (var row in new View[] { newItem, account, inbox, drafts, header, more }) content.AddView(row);
    }

    private void AddCommon(LinearLayout content)
    {
        Section(content, "Info bars, chips, badges, avatars");
        var padded = new LinearLayout(this) { Orientation = Orientation.Vertical };
        int side = WinoUnits.Dp(this, 12);
        padded.SetPadding(side, 0, side, 0);
        content.AddView(padded);

        var warning = new WinoInfoBar(this) { Severity = WinoInfoBarSeverity.Warning, Title = "Sign in required.", Message = "Wino can't sync this account until you sign in again.", ActionTitle = "Sign in" };
        var success = new WinoInfoBar(this) { Severity = WinoInfoBarSeverity.Success, Message = "Showing unread messages only. 12 messages.", IsClosable = true };
        padded.AddView(warning, Spaced());
        padded.AddView(success, Spaced());

        var chips = new Google.Android.Material.Chip.ChipGroup(this);
        chips.AddView(WinoChips.Assist(this, "All", WinoIconGlyph.Filter));
        chips.AddView(WinoChips.Filter(this, "Unread", isChecked: true));
        chips.AddView(WinoChips.Input(this, "Ayşe Demir", "ayse@example.com"));
        chips.AddView(WinoChips.Pill(this, "Action needed", WinoIconGlyph.Sparkle));
        padded.AddView(chips, Spaced());
        padded.AddView(new WinoUnreadPill(this) { Count = 12 }, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { TopMargin = WinoUnits.Dp(this, 8) });

        var avatars = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        foreach (var (provider, color) in new[] { (AccountIconProvider.Microsoft, "#2C3E50"), (AccountIconProvider.Google, "#16A085"), (AccountIconProvider.Imap, "#7F8C8D") })
        {
            var icon = new WinoAccountIconView(this) { DisplayName = provider == AccountIconProvider.Imap ? "Wino" : "Burak Köse", Account = new AccountIconInfo(provider, null, color) };
            var layout = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginEnd = WinoUnits.Dp(this, 20) };
            avatars.AddView(icon, layout);
        }
        var picture = new WinoContactPicture(this);
        picture.SetIdentity("Stripe", "receipts@stripe.com");
        avatars.AddView(picture);
        padded.AddView(avatars, Spaced());

        var buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        buttons.AddView(SettingsBinder.CreateButton(this, "Send", outlined: false, WinoIconGlyph.Send));
        var outlined = SettingsBinder.CreateButton(this, "Save draft");
        buttons.AddView(outlined, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginStart = WinoUnits.Dp(this, 8) });
        padded.AddView(buttons, Spaced());
    }

    private void AddSettings(LinearLayout content)
    {
        var group = new WinoSettingsGroup(this, "Settings cards", "Rows are 64 dp, radius 4, 4 dp apart.");
        int side = WinoUnits.Dp(this, 12);
        group.SetPadding(side, 0, side, 0);
        var markRead = new WinoSettingsCard(this, "Mark as read", "When the message opens in the reader", WinoIconGlyph.MarkRead,
            new Google.Android.Material.MaterialSwitch.MaterialSwitch(this) { Checked = true });
        var swipe = new WinoSettingsExpander(this, "Swipe actions", "Choose what each direction does", WinoIconGlyph.ArrowSwap, isExpanded: true);
        swipe.Add(new WinoSettingsCard(this, "Swipe right", content: SettingsBinder.CreateButton(this, "Archive")));
        swipe.Add(new WinoSettingsCard(this, "Swipe left", content: SettingsBinder.CreateButton(this, "Delete")));
        var personalization = new WinoSettingsCard(this, "Personalization", null, WinoIconGlyph.PaintBrush) { IsClickable = true, ShowsChevron = true };
        group.AddRange(markRead, swipe, personalization);
        content.AddView(group);
    }

    private void Section(LinearLayout content, string title)
    {
        var label = new TextView(this) { Text = title };
        label.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12);
        label.SetTextColor(WinoColors.SecondaryText(this));
        label.SetPadding(WinoUnits.Dp(this, 16), WinoUnits.Dp(this, 14), WinoUnits.Dp(this, 16), WinoUnits.Dp(this, 6));
        content.AddView(label);
    }

    private LinearLayout.LayoutParams Spaced()
        => new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = WinoUnits.Dp(this, 8) };

    /// <summary>A deterministic mail item for the gallery list.</summary>
    private sealed class SampleMail(string sender, string address, string subject, string preview, DateTimeOffset date) : IMailListSourceItem
    {
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public Guid StableId { get; } = Guid.NewGuid();
        public string? ThreadKey => null;
        public DateTimeOffset DateSortKey { get; } = date;
        public string NameSortKey => Sender;
        public bool IsPinned => false;
        public string Sender { get; } = sender;
        public string Address { get; } = address;
        public string Subject { get; } = subject;
        public string Preview { get; } = preview;
        public bool IsUnread { get; init; }
        public bool IsFlagged { get; init; }
        public bool IsDraft { get; init; }
        public bool HasAttachments { get; init; }
        public (string Name, global::Android.Graphics.Color Color)? Category { get; init; }
    }
}
#endif
