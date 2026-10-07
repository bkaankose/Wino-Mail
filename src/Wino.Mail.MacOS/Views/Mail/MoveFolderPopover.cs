using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// "Move to folder" popover (design board "Dialogs"): a search field over an outline of the
/// account's folders in navigation order. Typing filters to matching folders; Return or a click
/// picks the folder. The source folder and virtual groups (More, Categories) are not selectable.
/// </summary>
internal sealed class MoveFolderPopover : NSObject
{
    private readonly NSPopover _popover;
    private readonly List<FolderNode> _roots;
    private readonly IReadOnlySet<Guid> _sourceFolderIds;
    private readonly Action<IMailItemFolder> _picked;
    private readonly NSOutlineView _outline;
    private readonly FolderSource _source;
    private readonly FolderDelegate _delegate;
    private List<FolderNode> _visibleRoots;
    private readonly NSSearchField _search;
    private bool _closed;

    public MoveFolderPopover(IReadOnlyList<IMailItemFolder> folders, IReadOnlySet<Guid> sourceFolderIds, Action<IMailItemFolder> picked)
    {
        _roots = folders.Where(static folder => folder is not null).Select(static folder => new FolderNode(folder)).ToList();
        _visibleRoots = _roots;
        _sourceFolderIds = sourceFolderIds;
        _picked = picked;

        var search = _search = new NSSearchField { PlaceholderString = Translator.ContextFlyout_SearchPlaceholder, TranslatesAutoresizingMaskIntoConstraints = false };
        search.SendsSearchStringImmediately = true;
        search.Changed += (_, _) => ApplyFilter(search.StringValue);
        search.Activated += (_, _) => PickFirstVisible();

        _outline = new NSOutlineView
        {
            HeaderView = null,
            Style = NSTableViewStyle.SourceList,
            RowHeight = 24,
            IndentationPerLevel = 14,
            AllowsEmptySelection = true,
            AllowsMultipleSelection = false
        };
        var column = new NSTableColumn("folder") { ResizingMask = NSTableColumnResizing.Autoresizing };
        _outline.AddColumn(column);
        _outline.OutlineTableColumn = column;
        _source = new FolderSource(this);
        _delegate = new FolderDelegate(this);
        _outline.DataSource = _source;
        _outline.Delegate = _delegate;
        WinoAccessibility.Label(_outline, Translator.MailOperation_Move);

        var scroll = new NSScrollView { DocumentView = _outline, HasVerticalScroller = true, DrawsBackground = false, TranslatesAutoresizingMaskIntoConstraints = false };
        var title = WinoStyle.Label(Translator.MailOperation_Move, WinoStyle.BodyStrong);
        var stack = WinoLayout.VStack(8, title, search, scroll);
        stack.EdgeInsets = new NSEdgeInsets(12, 12, 12, 12);
        search.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -24).Active = true;
        scroll.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -24).Active = true;
        scroll.HeightAnchor.ConstraintEqualTo(320).Active = true;
        var root = new NSView(new CGRect(0, 0, 300, 400));
        WinoLayout.Fill(stack, root);

        _popover = new NSPopover
        {
            Behavior = NSPopoverBehavior.Transient,
            Animates = true,
            ContentSize = new CGSize(300, 400),
            ContentViewController = new NSViewController { View = root }
        };
        _outline.ReloadData();
        foreach (var node in _roots) _outline.ExpandItem(node, false);
    }

    public bool IsShown => _popover.Shown;

    public void Show(NSView anchor)
    {
        _popover.Show(anchor.Bounds, anchor, NSRectEdge.MaxYEdge);
        _search.Window?.MakeFirstResponder(_search);
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        if (_popover.Shown) _popover.Close();
    }

    private bool IsSelectable(FolderNode node) => node.Folder.IsMoveTarget && !_sourceFolderIds.Contains(node.Folder.Id);

    private void ApplyFilter(string text)
    {
        text = text?.Trim() ?? string.Empty;
        _visibleRoots = text.Length == 0
            ? _roots
            : Flatten(_roots).Where(node => IsSelectable(node) && (node.Folder.FolderName ?? string.Empty).Contains(text, StringComparison.CurrentCultureIgnoreCase))
                .Select(static node => new FolderNode(node.Folder, flat: true)).ToList();
        _outline.ReloadData();
        if (text.Length == 0) foreach (var node in _roots) _outline.ExpandItem(node, false);
    }

    private static IEnumerable<FolderNode> Flatten(IEnumerable<FolderNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    private void PickFirstVisible()
    {
        var first = Flatten(_visibleRoots).FirstOrDefault(IsSelectable);
        if (first is not null) Pick(first);
    }

    private void Pick(FolderNode node)
    {
        if (!IsSelectable(node)) return;
        Close();
        _picked(node.Folder);
    }

    /// <summary>Folder glyph, the same map as XamlHelpers.GetSpecialFolderPathIconGeometry on Windows.</summary>
    private static WinoIconGlyph Glyph(SpecialFolderType type) => type switch
    {
        SpecialFolderType.Inbox => WinoIconGlyph.SpecialFolderInbox,
        SpecialFolderType.Starred => WinoIconGlyph.SpecialFolderStarred,
        SpecialFolderType.Important => WinoIconGlyph.SpecialFolderImportant,
        SpecialFolderType.Sent => WinoIconGlyph.SpecialFolderSent,
        SpecialFolderType.Draft => WinoIconGlyph.SpecialFolderDraft,
        SpecialFolderType.Archive => WinoIconGlyph.SpecialFolderArchive,
        SpecialFolderType.Deleted => WinoIconGlyph.SpecialFolderDeleted,
        SpecialFolderType.Junk => WinoIconGlyph.SpecialFolderJunk,
        SpecialFolderType.Chat => WinoIconGlyph.SpecialFolderChat,
        SpecialFolderType.Category => WinoIconGlyph.SpecialFolderCategory,
        SpecialFolderType.Unread => WinoIconGlyph.SpecialFolderUnread,
        SpecialFolderType.Forums => WinoIconGlyph.SpecialFolderForums,
        SpecialFolderType.Updates => WinoIconGlyph.SpecialFolderUpdated,
        SpecialFolderType.Personal => WinoIconGlyph.SpecialFolderPersonal,
        SpecialFolderType.Promotions => WinoIconGlyph.SpecialFolderPromotions,
        SpecialFolderType.Social => WinoIconGlyph.SpecialFolderSocial,
        SpecialFolderType.Other => WinoIconGlyph.SpecialFolderOther,
        SpecialFolderType.More => WinoIconGlyph.SpecialFolderMore,
        _ => WinoIconGlyph.Folder
    };

    private sealed class FolderNode : NSObject
    {
        public FolderNode(IMailItemFolder folder, bool flat = false)
        {
            Folder = folder;
            Children = flat ? [] : (folder.ChildFolders ?? []).Where(static child => child is not null).Select(static child => new FolderNode(child)).ToList();
        }

        public IMailItemFolder Folder { get; }
        public List<FolderNode> Children { get; }
    }

    private sealed class FolderSource(MoveFolderPopover owner) : NSOutlineViewDataSource
    {
        private List<FolderNode> ChildrenOf(NSObject? item) => item is FolderNode node ? node.Children : owner._visibleRoots;
        public override nint GetChildrenCount(NSOutlineView outlineView, NSObject? item) => ChildrenOf(item).Count;
        public override NSObject GetChild(NSOutlineView outlineView, nint childIndex, NSObject? item) => ChildrenOf(item)[(int)childIndex];
        public override bool ItemExpandable(NSOutlineView outlineView, NSObject item) => item is FolderNode { Children.Count: > 0 };
    }

    private sealed class FolderDelegate(MoveFolderPopover owner) : NSOutlineViewDelegate
    {
        public override NSView GetView(NSOutlineView outlineView, NSTableColumn? tableColumn, NSObject item)
        {
            var node = (FolderNode)item;
            bool selectable = owner.IsSelectable(node);
            var glyph = new WinoIconView(Glyph(node.Folder.SpecialFolderType), 14, selectable ? WinoStyle.Accent : WinoStyle.TertiaryText);
            var label = WinoStyle.Label(node.Folder.FolderName, WinoStyle.Body, selectable ? WinoStyle.PrimaryText : WinoStyle.TertiaryText);
            var row = WinoLayout.HStack(6, glyph, label);
            var cell = new NSTableCellView();
            WinoLayout.Fill(row, cell, 0, 2, 0, 4);
            cell.AccessibilityLabel = node.Folder.FolderName;
            return cell;
        }

        public override bool ShouldSelectItem(NSOutlineView outlineView, NSObject item) => item is FolderNode node && owner.IsSelectable(node);

        public override void SelectionDidChange(NSNotification notification)
        {
            if (owner._outline.ItemAtRow(owner._outline.SelectedRow) is FolderNode node) owner.Pick(node);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
            _outline.DataSource = null!;
            _outline.Delegate = null!;
            _source.Dispose();
            _delegate.Dispose();
        }
        base.Dispose(disposing);
    }
}
