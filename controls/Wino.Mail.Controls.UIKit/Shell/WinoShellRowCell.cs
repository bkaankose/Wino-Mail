using Foundation;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core.AccountIcon;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Shell;

/// <summary>What a <see cref="WinoShellRowCell"/> shows.</summary>
public enum WinoShellRowKind
{
    /// <summary>The accent "New mail" / "New event" row at the top of the pane.</summary>
    NewItem,
    /// <summary>An account: its icon, name and address, the unread pill and the sync bar.</summary>
    Account,
    /// <summary>A folder (or any glyph row): glyph, title and unread pill, indented by depth.</summary>
    Folder,
    /// <summary>A section caption ("Favorites", "More").</summary>
    Header
}

/// <summary>
/// A shell pane row for the iPad sidebar and the phone Mailboxes list (UIKit counterpart of the AppKit shell pane rows
/// and <c>WinoShellRowView</c>). Selection is the Windows NavigationViewItem look: a subtle rounded fill inset from the
/// pane edges and a 3pt accent pipe on the leading edge, never the system highlight. <see cref="ShowsIndicator"/>
/// draws the pipe alone, which marks the active account while a folder below it holds the selection.
/// </summary>
public sealed class WinoShellRowCell : UITableViewCell
{
    public new const string ReuseIdentifier = "WinoShellRow";

    /// <summary>The New item row height (Windows NewMailButton).</summary>
    public const double NewItemHeight = 50;

    /// <summary>The account row height.</summary>
    public const double AccountHeight = 50;

    /// <summary>Folder rows: the Mac 28pt row raised to the touch target.</summary>
    public const double FolderHeight = 44;

    public const double HeaderHeight = 32;
    public const double HorizontalInset = 6;
    public const double CornerRadius = 5;
    public const double DepthIndent = 14;

    private readonly WinoSurfaceView _fill;
    private readonly WinoSurfaceView _pipe;
    private readonly WinoSurfaceView _newItemSurface;
    private readonly WinoIconView _glyph;
    private readonly WinoAccountIconView _accountIcon;
    private readonly UILabel _title;
    private readonly UILabel _subtitle;
    private readonly UIStackView _textColumn;
    private readonly WinoUnreadPill _pill;
    private readonly WinoIconView _disclosure;
    private readonly WinoSyncBar _syncBar;
    private readonly UIStackView _row;
    private readonly NSLayoutConstraint _rowLeading;
    private readonly NSLayoutConstraint _pipeHeight;
    private WinoShellRowKind _kind = WinoShellRowKind.Folder;
    private bool _showsIndicator;
    private bool _highlightable = true;
    private bool _isDropTarget;

    public WinoShellRowCell() : this(UITableViewCellStyle.Default, new NSString(ReuseIdentifier)) { }

    [Export("initWithStyle:reuseIdentifier:")]
    public WinoShellRowCell(UITableViewCellStyle style, NSString reuseIdentifier) : base(style, reuseIdentifier)
    {
#if DEBUG
        Wino.Presentation.UIKit.WinoLiveObjects.Track(this);
#endif
        SelectionStyle = UITableViewCellSelectionStyle.None;
        BackgroundColor = UIColor.Clear;

        _fill = new WinoSurfaceView { CornerRadius = CornerRadius, Hidden = true };
        WinoLayout.Fill(_fill, ContentView, 0.5, HorizontalInset, 0.5, HorizontalInset);
        _pipe = new WinoSurfaceView { CornerRadius = 1.5, Fill = WinoStyle.Accent, Hidden = true };
        ContentView.AddSubview(_pipe);
        _pipeHeight = _pipe.HeightAnchor.ConstraintEqualTo(16);
        NSLayoutConstraint.ActivateConstraints(
        [
            _pipe.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor, 4 + (nfloat)HorizontalInset - 2),
            _pipe.WidthAnchor.ConstraintEqualTo(3),
            _pipe.CenterYAnchor.ConstraintEqualTo(ContentView.CenterYAnchor),
            _pipeHeight
        ]);

        _newItemSurface = new WinoSurfaceView { CornerRadius = WinoStyle.ControlRadius, Fill = WinoStyle.Accent, Hidden = true };
        WinoLayout.Fill(_newItemSurface, ContentView, 6, 10, 6, 10);

        _glyph = new WinoIconView(WinoIconGlyph.Folder, 16);
        WinoLayout.Size(_glyph, 20, 20);
        _accountIcon = new WinoAccountIconView(28) { Hidden = true };
        _title = WinoStyle.Label(string.Empty, WinoStyle.Body);
        WinoLayout.Priorities(_title, UILayoutConstraintAxis.Horizontal, 1, 250);
        _subtitle = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
        _subtitle.Hidden = true;
        _syncBar = new WinoSyncBar { Hidden = true };
        _textColumn = WinoLayout.VStack(1, _title, _subtitle, _syncBar);
        _textColumn.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Priorities(_textColumn, UILayoutConstraintAxis.Horizontal, 1, 250);
        _pill = new WinoUnreadPill();
        _disclosure = new WinoIconView(WinoIconGlyph.ChevronRight, 10, WinoStyle.SecondaryText) { Colorful = false, Hidden = true };
        WinoLayout.Size(_disclosure, 12, 12);

        _row = WinoLayout.HStack(10, _glyph, _accountIcon, _textColumn, _pill, _disclosure);
        _row.Distribution = UIStackViewDistribution.Fill;
        ContentView.AddSubview(_row);
        _row.TranslatesAutoresizingMaskIntoConstraints = false;
        _rowLeading = _row.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor, 18);
        NSLayoutConstraint.ActivateConstraints(
        [
            _rowLeading,
            _row.TrailingAnchor.ConstraintEqualTo(ContentView.TrailingAnchor, -16),
            _row.CenterYAnchor.ConstraintEqualTo(ContentView.CenterYAnchor),
            _row.TopAnchor.ConstraintGreaterThanOrEqualTo(ContentView.TopAnchor, 4),
            _row.BottomAnchor.ConstraintLessThanOrEqualTo(ContentView.BottomAnchor, -4)
        ]);

        IsAccessibilityElement = true;
        WinoStyle.AccentWatchers.Add(this, static view => view.AccentChanged(null, EventArgs.Empty));
        ApplySelection();
    }

    public WinoShellRowKind Kind => _kind;

    /// <summary>The row's preferred height for its kind.</summary>
    public static double HeightFor(WinoShellRowKind kind) => kind switch
    {
        WinoShellRowKind.NewItem => NewItemHeight,
        WinoShellRowKind.Account => AccountHeight,
        WinoShellRowKind.Header => HeaderHeight,
        _ => FolderHeight
    };

    /// <summary>Owner data, typically the shell menu item this cell shows.</summary>
    public object? Item { get; set; }

    /// <summary>The accent New item row: white glyph and title on the accent.</summary>
    public void ConfigureNewItem(string title, WinoIconGlyph glyph = WinoIconGlyph.NewMail)
    {
        Reset(WinoShellRowKind.NewItem);
        _newItemSurface.Hidden = false;
        _newItemSurface.Fill = WinoStyle.Accent;
        _glyph.Icon = glyph;
        _glyph.Tint = UIColor.White;
        _glyph.Colorful = false;
        _title.Text = title;
        _title.Font = WinoStyle.BodyStrong;
        _title.TextColor = UIColor.White;
        _rowLeading.Constant = 22;
        AccessibilityLabel = title;
        AccessibilityTraits = UIAccessibilityTrait.Button;
    }

    /// <summary>An account row: 28pt icon, name and optional address, unread pill and sync bar.</summary>
    public void ConfigureAccount(IAccountIconInfo account, string name, string? address = null, int unreadCount = 0, bool isProfilePictureEnabled = true)
    {
        Reset(WinoShellRowKind.Account);
        _glyph.Hidden = true;
        _accountIcon.Hidden = false;
        _accountIcon.IsProfilePictureEnabled = isProfilePictureEnabled;
        _accountIcon.Account = account;
        _title.Text = name;
        _title.Font = WinoStyle.BodyStrong;
        _subtitle.Text = address ?? string.Empty;
        _subtitle.Hidden = string.IsNullOrEmpty(address);
        _pill.Count = unreadCount;
        AccessibilityLabel = string.IsNullOrEmpty(address) ? name : $"{name}, {address}";
        AccessibilityValue = unreadCount > 0 ? _pill.AccessibilityLabel : null;
    }

    /// <summary>A folder row: glyph, title and unread pill, indented by <paramref name="depth"/>, with an optional disclosure.</summary>
    public void ConfigureFolder(WinoIconGlyph glyph, string title, int unreadCount = 0, int depth = 0, bool? isExpanded = null)
    {
        Reset(WinoShellRowKind.Folder);
        _glyph.Icon = glyph;
        _title.Text = title;
        _pill.Count = unreadCount;
        _rowLeading.Constant = (nfloat)(18 + Math.Max(0, depth) * DepthIndent);
        _disclosure.Hidden = isExpanded is null;
        if (isExpanded is { } expanded) _disclosure.Icon = expanded ? WinoIconGlyph.ChevronDown : WinoIconGlyph.ChevronRight;
        AccessibilityLabel = title;
        AccessibilityValue = unreadCount > 0 ? _pill.AccessibilityLabel : null;
    }

    /// <summary>A section caption.</summary>
    public void ConfigureHeader(string title)
    {
        Reset(WinoShellRowKind.Header);
        _glyph.Hidden = true;
        _title.Text = title;
        _title.Font = WinoStyle.CaptionStrong;
        _title.TextColor = WinoStyle.SecondaryText;
        Highlightable = false;
        AccessibilityLabel = title;
        AccessibilityTraits = UIAccessibilityTrait.Header;
    }

    /// <summary>The account sync bar; hidden unless the account synchronizes. <paramref name="value"/> is 0 to 100.</summary>
    public void SetSync(bool visible, bool indeterminate = true, double value = 0)
    {
        _syncBar.Hidden = !visible || _kind != WinoShellRowKind.Account;
        if (!_syncBar.Hidden) _syncBar.Update(indeterminate, value);
    }

    private void Reset(WinoShellRowKind kind)
    {
        _kind = kind;
        _newItemSurface.Hidden = true;
        _glyph.Hidden = false;
        _glyph.Tint = null;
        _glyph.Colorful = null;
        _accountIcon.Hidden = true;
        _subtitle.Hidden = true;
        _syncBar.Hidden = true;
        _disclosure.Hidden = true;
        _pill.Count = 0;
        _title.Font = WinoStyle.Body;
        _title.TextColor = WinoStyle.PrimaryText;
        _rowLeading.Constant = 18;
        _highlightable = kind is WinoShellRowKind.Account or WinoShellRowKind.Folder;
        AccessibilityValue = null;
        AccessibilityTraits = UIAccessibilityTrait.Button;
        ApplySelection();
    }

    /// <summary>Draws the accent pipe even while the row is not selected (the active account).</summary>
    public bool ShowsIndicator
    {
        get => _showsIndicator;
        set { if (_showsIndicator == value) return; _showsIndicator = value; ApplySelection(); }
    }

    /// <summary>False for rows that never show a selection fill (headers, the New item row).</summary>
    public bool Highlightable
    {
        get => _highlightable;
        set { if (_highlightable == value) return; _highlightable = value; ApplySelection(); }
    }

    /// <summary>
    /// Marks the row as the target of a drag in progress (iPad drag and drop): an accent fill at 16% with a 2pt accent
    /// ring (the Windows IsDraggingItemOver state).
    /// </summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set { if (_isDropTarget == value) return; _isDropTarget = value; ApplySelection(); }
    }

    public override void SetSelected(bool selected, bool animated)
    {
        base.SetSelected(selected, animated);
        ApplySelection();
    }

    public override void SetHighlighted(bool highlighted, bool animated)
    {
        base.SetHighlighted(highlighted, animated);
        ApplySelection();
    }

    private void ApplySelection()
    {
        bool selected = Selected && _highlightable;
        bool pressed = Highlighted && !Selected && _highlightable;
        if (_kind == WinoShellRowKind.NewItem)
        {
            _newItemSurface.Alpha = Highlighted ? 0.75f : 1;
            _fill.Hidden = true;
            _pipe.Hidden = true;
            return;
        }

        _fill.Hidden = !(selected || pressed || _isDropTarget);
        if (_isDropTarget)
        {
            _fill.Fill = WinoStyle.Accent.ColorWithAlpha(0.16f);
            _fill.Stroke = WinoStyle.Accent;
            _fill.StrokeWidth = 2;
        }
        else
        {
            _fill.Fill = pressed ? UIColor.Label.ColorWithAlpha(0.045f) : WinoStyle.SelectedFill;
            _fill.Stroke = null;
        }
        _pipe.Hidden = !(selected || _showsIndicator);
        _pipe.Fill = WinoStyle.Accent;
        if (selected) AccessibilityTraits |= UIAccessibilityTrait.Selected;
        else AccessibilityTraits &= ~UIAccessibilityTrait.Selected;
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        var height = ContentView.Bounds.Height;
        var inset = Math.Min(11, height / 4);
        var pipe = (nfloat)Math.Max(0, height - inset * 2);
        if (_pipeHeight.Constant != pipe) _pipeHeight.Constant = pipe;
    }

    private void AccentChanged(object? sender, EventArgs args)
    {
        if (_kind == WinoShellRowKind.NewItem) _newItemSurface.Fill = WinoStyle.Accent;
        ApplySelection();
    }

    public override void PrepareForReuse()
    {
        base.PrepareForReuse();
        Item = null;
        _showsIndicator = false;
        _isDropTarget = false;
        ApplySelection();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoStyle.AccentWatchers.Remove(this);
        base.Dispose(disposing);
    }
}
