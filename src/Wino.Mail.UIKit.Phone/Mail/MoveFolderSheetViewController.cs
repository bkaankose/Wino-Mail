using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.UIKit.Shell;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Mail;

/// <summary>
/// The phone "Move to folder" sheet (twin of the Mac MoveFolderPopover): the account's folders in navigation order as
/// indented Wino folder rows, with a search field that filters to matching folders. The source folder and virtual groups
/// (More, Categories) are shown but not selectable (<see cref="IMailItemFolder.IsMoveTarget"/>). A tap picks the folder;
/// Cancel or a swipe down leaves the mail where it is.
/// </summary>
public sealed class MoveFolderSheetViewController : UIViewController
{
    private readonly IReadOnlyList<(IMailItemFolder Folder, int Depth)> _all;
    private readonly IReadOnlySet<Guid> _sourceFolderIds;
    private readonly Action<IMailItemFolder> _picked;
    private readonly Action _cancel;
    private readonly UITableView _table = new(default, UITableViewStyle.Plain) { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UISearchBar _search = new() { SearchBarStyle = UISearchBarStyle.Minimal, TranslatesAutoresizingMaskIntoConstraints = false };
    private List<(IMailItemFolder Folder, int Depth)> _visible;

    public MoveFolderSheetViewController(IReadOnlyList<IMailItemFolder> folders, IReadOnlySet<Guid> sourceFolderIds, Action<IMailItemFolder> picked, Action cancel)
    {
        var flat = new List<(IMailItemFolder, int)>();
        Flatten(folders, 0, flat);
        _all = flat;
        _visible = flat;
        _sourceFolderIds = sourceFolderIds;
        _picked = picked;
        _cancel = cancel;
        Title = Translator.MailOperation_Move;
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
        var root = new UIView { BackgroundColor = UIColor.SystemBackground };
        _search.Placeholder = Translator.ContextFlyout_SearchPlaceholder;
        _search.TextChanged += (_, args) => ApplyFilter(args.SearchText);
        _search.SearchButtonClicked += (_, _) => PickFirstVisible();
        _table.BackgroundColor = UIColor.Clear;
        _table.SeparatorStyle = UITableViewCellSeparatorStyle.None;
        _table.KeyboardDismissMode = UIScrollViewKeyboardDismissMode.OnDrag;
        _table.RegisterClassForCellReuse(typeof(WinoShellRowCell), WinoShellRowCell.ReuseIdentifier);
        _table.Source = new FolderSource(this);
        WinoAccessibility.Label(_table, Translator.MailOperation_Move);
        var column = WinoLayout.VStack(0, _search, _table);
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
        View = root;
    }

    private bool IsSelectable(IMailItemFolder folder) => folder.IsMoveTarget && !_sourceFolderIds.Contains(folder.Id);

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

    private sealed class FolderSource(MoveFolderSheetViewController owner) : UITableViewSource
    {
        public override nint RowsInSection(UITableView tableview, nint section) => owner._visible.Count;

        public override nfloat GetHeightForRow(UITableView tableView, NSIndexPath indexPath) => (nfloat)WinoShellRowCell.FolderHeight;

        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            var cell = (WinoShellRowCell)tableView.DequeueReusableCell(WinoShellRowCell.ReuseIdentifier, indexPath);
            var (folder, depth) = owner._visible[indexPath.Row];
            cell.ConfigureFolder(SpecialFolderGlyphs.GetGlyphOrFolder(folder.SpecialFolderType), folder.FolderName ?? string.Empty, 0, depth);
            bool selectable = owner.IsSelectable(folder);
            cell.ContentView.Alpha = selectable ? 1 : 0.4f;
            cell.Highlightable = selectable;
            cell.AccessibilityTraits = selectable ? UIAccessibilityTrait.Button : UIAccessibilityTrait.NotEnabled;
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
