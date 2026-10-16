using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Shell;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Mail;

/// <summary>
/// The phone "Move to folder" sheet (twin of the Mac MoveFolderPopover; MoveFolder board of 15 Oct 2026): a title with the
/// message count and subject, a filter field, then the account's folders in navigation order as indented Wino folder rows
/// in one inset-grouped card under the account's header (avatar, name, address). The source folder reads
/// "Current folder" and, like virtual groups (More, Categories), is not selectable (<see cref="IMailItemFolder.IsMoveTarget"/>).
/// A tap picks the folder; Cancel or a swipe down leaves the mail where it is.
/// </summary>
public sealed class MoveFolderSheetViewController : UIViewController
{
    private readonly IReadOnlyList<(IMailItemFolder Folder, int Depth)> _all;
    private readonly IReadOnlySet<Guid> _sourceFolderIds;
    private readonly MoveFolderSheetContext _context;
    private readonly Action<IMailItemFolder> _picked;
    private readonly Action _cancel;
    private readonly UITableView _table = new(default, UITableViewStyle.InsetGrouped) { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UISearchBar _search = new() { SearchBarStyle = UISearchBarStyle.Minimal, TranslatesAutoresizingMaskIntoConstraints = false };
    private List<(IMailItemFolder Folder, int Depth)> _visible;

    public MoveFolderSheetViewController(IReadOnlyList<IMailItemFolder> folders, IReadOnlySet<Guid> sourceFolderIds, MoveFolderSheetContext context,
        Action<IMailItemFolder> picked, Action cancel)
    {
        var flat = new List<(IMailItemFolder, int)>();
        Flatten(folders, 0, flat);
        _all = flat;
        _visible = flat;
        _sourceFolderIds = sourceFolderIds;
        _context = context;
        _picked = picked;
        _cancel = cancel;
        Title = Translator.IOS_MoveSheet_Title;
    }

    private static void Flatten(IEnumerable<IMailItemFolder>? folders, int depth, List<(IMailItemFolder, int)> into)
    {
        foreach (var folder in folders ?? [])
        {
            if (folder is null) continue;
            into.Add((folder, depth));
            Flatten(folder.ChildFolders, depth + 1, into);
        }
    }

    public override void LoadView()
    {
        var root = new UIView { BackgroundColor = UIColor.SystemGroupedBackground };
        _search.Placeholder = Translator.ContextFlyout_SearchPlaceholder;
        _search.TextChanged += (_, args) => ApplyFilter(args.SearchText);
        _search.SearchButtonClicked += (_, _) => PickFirstVisible();
        _table.BackgroundColor = UIColor.Clear;
        _table.SectionHeaderTopPadding = 0;
        _table.KeyboardDismissMode = UIScrollViewKeyboardDismissMode.OnDrag;
        _table.RegisterClassForCellReuse(typeof(WinoShellRowCell), WinoShellRowCell.ReuseIdentifier);
        _table.Source = new FolderSource(this);
        WinoAccessibility.Label(_table, Translator.IOS_MoveSheet_Title);
        var searchRow = WinoLayout.Insets(WinoLayout.VStack(0, _search), 0, 8, 0, 8);
        searchRow.Alignment = UIStackViewAlignment.Fill;
        var column = WinoLayout.VStack(0, searchRow, _table);
        column.Alignment = UIStackViewAlignment.Fill;
        root.AddSubview(column);
        var guide = root.SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(
        [
            column.TopAnchor.ConstraintEqualTo(guide.TopAnchor),
            column.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor),
            column.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor),
            column.BottomAnchor.ConstraintEqualTo(root.BottomAnchor)
        ]);
        NavigationItem.LeftBarButtonItem = new UIBarButtonItem(Translator.Buttons_Cancel, UIBarButtonItemStyle.Plain, (_, _) => _cancel());
        NavigationItem.TitleView = TitleView();
        View = root;
    }

    /// <summary>"Move to folder" over "1 message · Subject" (the board's two-line sheet title).</summary>
    private UIView TitleView()
    {
        var title = WinoStyle.Label(Translator.IOS_MoveSheet_Title, WinoStyle.Fixed(17, UIFontWeight.Semibold));
        title.TextAlignment = UITextAlignment.Center;
        var count = _context.MessageCount == 1 ? Translator.IOS_MoveSheet_OneMessage : string.Format(Translator.IOS_MoveSheet_Messages, _context.MessageCount);
        var subtitleText = string.IsNullOrWhiteSpace(_context.Subject) ? count : $"{count} · {_context.Subject}";
        var subtitle = WinoStyle.Label(subtitleText, WinoStyle.Fixed(12), WinoStyle.SecondaryText);
        subtitle.TextAlignment = UITextAlignment.Center;
        subtitle.LineBreakMode = UILineBreakMode.TailTruncation;
        var stack = WinoLayout.VStack(0, title, subtitle);
        stack.Alignment = UIStackViewAlignment.Center;
        subtitle.WidthAnchor.ConstraintLessThanOrEqualTo(220).Active = true;
        stack.IsAccessibilityElement = true;
        stack.AccessibilityTraits = UIAccessibilityTrait.Header;
        stack.AccessibilityLabel = $"{Translator.IOS_MoveSheet_Title}, {subtitleText}";
        return stack;
    }

    private bool IsSource(IMailItemFolder folder) => _sourceFolderIds.Contains(folder.Id);

    private bool IsSelectable(IMailItemFolder folder) => folder.IsMoveTarget && !IsSource(folder);

    private void ApplyFilter(string? text)
    {
        text = text?.Trim() ?? string.Empty;
        _visible = text.Length == 0
            ? _all.ToList()
            : _all.Where(entry => IsSelectable(entry.Folder) && (entry.Folder.FolderName ?? string.Empty).Contains(text, StringComparison.CurrentCultureIgnoreCase))
                .Select(static entry => (entry.Folder, 0)).ToList();
        _table.ReloadData();
    }

    private void PickFirstVisible()
    {
        foreach (var (folder, _) in _visible)
            if (IsSelectable(folder)) { _picked(folder); return; }
    }

    /// <summary>The account header above the folder card: a 22pt avatar, the account name and its address trailing.</summary>
    private UIView AccountHeader()
    {
        var picture = new WinoContactPicture(22);
        picture.SetIdentity(_context.AccountName, _context.AccountAddress);
        var name = WinoStyle.Label(_context.AccountName, WinoStyle.Scaled(13, UIFontWeight.Semibold));
        WinoLayout.Priorities(name, UILayoutConstraintAxis.Horizontal, 1, 250);
        var address = WinoStyle.Label(_context.AccountAddress, WinoStyle.Scaled(13), WinoStyle.SecondaryText);
        WinoLayout.Priorities(address, UILayoutConstraintAxis.Horizontal, 750, 240);
        var row = WinoLayout.Insets(WinoLayout.HStack(8, picture, name, WinoLayout.Spacer(), address), 12, 20, 6, 20);
        row.Alignment = UIStackViewAlignment.Center;
        row.IsAccessibilityElement = true;
        row.AccessibilityTraits = UIAccessibilityTrait.Header;
        row.AccessibilityLabel = string.Join(", ", new[] { _context.AccountName, _context.AccountAddress }.Where(static part => !string.IsNullOrWhiteSpace(part)));
        return row;
    }

    private sealed class FolderSource(MoveFolderSheetViewController owner) : UITableViewSource
    {
        public override nint RowsInSection(UITableView tableview, nint section) => owner._visible.Count;

        public override nfloat GetHeightForRow(UITableView tableView, NSIndexPath indexPath) => (nfloat)WinoShellRowCell.FolderHeight;

        public override nfloat GetHeightForHeader(UITableView tableView, nint section) => UITableView.AutomaticDimension;

        public override nfloat EstimatedHeightForHeader(UITableView tableView, nint section) => 40;

        public override UIView GetViewForHeader(UITableView tableView, nint section) => owner.AccountHeader();

        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            var cell = (WinoShellRowCell)tableView.DequeueReusableCell(WinoShellRowCell.ReuseIdentifier, indexPath);
            var (folder, depth) = owner._visible[indexPath.Row];
            cell.ConfigureFolder(SpecialFolderGlyphs.GetGlyphOrFolder(folder.SpecialFolderType), folder.FolderName ?? string.Empty, 0, depth);
            cell.BackgroundColor = UIColor.SecondarySystemGroupedBackground;
            bool selectable = owner.IsSelectable(folder);
            cell.ContentView.Alpha = selectable ? 1 : 0.4f;
            cell.Highlightable = selectable;
            cell.AccessibilityTraits = selectable ? UIAccessibilityTrait.Button : UIAccessibilityTrait.NotEnabled;
            if (owner.IsSource(folder))
            {
                var current = WinoStyle.Label(Translator.SearchBar_CurrentFolder, WinoStyle.Scaled(13), WinoStyle.SecondaryText);
                // An accessory view is placed by frame, not by Auto Layout.
                current.TranslatesAutoresizingMaskIntoConstraints = true;
                current.SizeToFit();
                cell.AccessoryView = current;
                cell.AccessibilityValue = Translator.SearchBar_CurrentFolder;
            }
            else
            {
                cell.AccessoryView = null;
                cell.AccessibilityValue = null;
            }
            return cell;
        }

        public override NSIndexPath? WillSelectRow(UITableView tableView, NSIndexPath indexPath)
            => indexPath.Row < owner._visible.Count && owner.IsSelectable(owner._visible[indexPath.Row].Folder) ? indexPath : null;

        public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
        {
            tableView.DeselectRow(indexPath, true);
            if (indexPath.Row < owner._visible.Count) owner._picked(owner._visible[indexPath.Row].Folder);
        }
    }
}

/// <summary>What the Move sheet says about the move: how many messages, the subject of a single one, and the account.</summary>
public sealed record MoveFolderSheetContext(int MessageCount, string? Subject, string? AccountName, string? AccountAddress);
