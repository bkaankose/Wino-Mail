using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.UIKit.Shell;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Pad.Mail;

/// <summary>
/// "Move to folder" popover (UIKit port of the Mac <c>MoveFolderPopover</c>): a search field over the account's folders
/// in navigation order, indented by depth. Typing filters to matching folders; Return or a tap picks the folder. The
/// source folder and virtual groups (More, Categories) are not selectable.
/// </summary>
public sealed class MoveFolderPopoverViewController : UIViewController
{
    public static readonly CGSize PreferredSize = new(320, 440);

    private readonly List<(IMailItemFolder Folder, int Depth)> _all = [];
    private readonly IReadOnlySet<Guid> _sourceFolderIds;
    private readonly Action<IMailItemFolder> _picked;
    private readonly UISearchTextField _search = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UITableView _table = new(CGRect.Empty, UITableViewStyle.Plain) { TranslatesAutoresizingMaskIntoConstraints = false };
    private List<(IMailItemFolder Folder, int Depth)> _visible = [];
    private bool _done;

    public MoveFolderPopoverViewController(IReadOnlyList<IMailItemFolder> folders, IReadOnlySet<Guid> sourceFolderIds, Action<IMailItemFolder> picked)
    {
        _sourceFolderIds = sourceFolderIds;
        _picked = picked;
        Flatten(folders, 0);
        _visible = _all;
    }

    private void Flatten(IEnumerable<IMailItemFolder>? folders, int depth)
    {
        foreach (var folder in folders ?? [])
        {
            if (folder is null) continue;
            _all.Add((folder, depth));
            Flatten(folder.ChildFolders, depth + 1);
        }
    }

    public override void LoadView()
    {
        var root = new UIView { BackgroundColor = UIColor.SystemBackground };
        var title = WinoStyle.Label(Translator.MailOperation_Move, WinoStyle.BodyStrong);
        _search.Placeholder = Translator.ContextFlyout_SearchPlaceholder;
        _search.ReturnKeyType = UIReturnKeyType.Done;
        _search.EditingChanged += (_, _) => ApplyFilter(_search.Text ?? string.Empty);
        _search.ShouldReturn = _ => { PickFirstVisible(); return false; };

        _table.BackgroundColor = UIColor.Clear;
        _table.SeparatorStyle = UITableViewCellSeparatorStyle.None;
        _table.RegisterClassForCellReuse(typeof(WinoShellRowCell), WinoShellRowCell.ReuseIdentifier);
        _table.Source = new FolderSource(this);
        WinoAccessibility.Label(_table, Translator.MailOperation_Move);

        var stack = WinoLayout.Insets(WinoLayout.VStack(8, title, _search, _table), 14, 12, 8, 12);
        stack.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(stack, root);
        View = root;
    }

    public override void ViewDidAppear(bool animated)
    {
        base.ViewDidAppear(animated);
        _search.BecomeFirstResponder();
    }

    private bool IsSelectable(IMailItemFolder folder) => folder.IsMoveTarget && !_sourceFolderIds.Contains(folder.Id);

    private void ApplyFilter(string text)
    {
        text = text.Trim();
        _visible = text.Length == 0
            ? _all
            : _all.Where(entry => IsSelectable(entry.Folder) && (entry.Folder.FolderName ?? string.Empty).Contains(text, StringComparison.CurrentCultureIgnoreCase))
                .Select(static entry => (entry.Folder, 0)).ToList();
        _table.ReloadData();
    }

    private void PickFirstVisible()
    {
        foreach (var (folder, _) in _visible)
        {
            if (!IsSelectable(folder)) continue;
            Pick(folder);
            return;
        }
    }

    private void Pick(IMailItemFolder folder)
    {
        if (_done || !IsSelectable(folder)) return;
        _done = true;
        PadShellHost.DismissPopover(this);
        _picked(folder);
    }

    private sealed class FolderSource(MoveFolderPopoverViewController owner) : UITableViewSource
    {
        public override nint RowsInSection(UITableView tableview, nint section) => owner._visible.Count;

        public override nfloat GetHeightForRow(UITableView tableView, NSIndexPath indexPath) => (nfloat)WinoShellRowCell.FolderHeight;

        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            var cell = (WinoShellRowCell)tableView.DequeueReusableCell(WinoShellRowCell.ReuseIdentifier, indexPath);
            var (folder, depth) = owner._visible[indexPath.Row];
            bool selectable = owner.IsSelectable(folder);
            cell.ConfigureFolder(SpecialFolderGlyphs.GetGlyphOrFolder(folder.SpecialFolderType), folder.FolderName ?? string.Empty, 0, depth);
            cell.Highlightable = selectable;
            cell.ContentView.Alpha = selectable ? 1 : 0.45f;
            if (!selectable) cell.AccessibilityTraits |= UIAccessibilityTrait.NotEnabled;
            cell.Item = folder;
            return cell;
        }

        public override NSIndexPath? WillSelectRow(UITableView tableView, NSIndexPath indexPath)
            => owner.IsSelectable(owner._visible[indexPath.Row].Folder) ? indexPath : null;

        public override void RowSelected(UITableView tableView, NSIndexPath indexPath) => owner.Pick(owner._visible[indexPath.Row].Folder);
    }
}
