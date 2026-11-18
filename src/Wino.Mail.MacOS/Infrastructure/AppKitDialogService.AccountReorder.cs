using System.Collections.ObjectModel;
using AppKit;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// The account order sheet (Windows AccountReorderDialog): top-level accounts and groups, each group with its own
/// ordered accounts below it. Rows move by drag or with their up and down buttons, and only among their siblings;
/// moving accounts between groups happens on the group page.
/// </summary>
public sealed partial class AppKitDialogService
{
    public async Task ShowAccountReorderDialogAsync(ObservableCollection<IAccountProviderDetailViewModel> availableAccounts)
    {
        if (services?.GetService<IAccountService>() is not { } accountService) return;

        ObservableCollection<AccountReorderItem> items;
        try
        {
            var accounts = await accountService.GetAccountsAsync();
            var groups = await accountService.GetAccountGroupsAsync();
            items = AccountReorderItem.FromLayout(AccountLayoutBuilder.Build(accounts, groups));
        }
        catch (Exception exception)
        {
            error(exception);
            return;
        }

        var pictures = services.GetService<IPictureStorageService>();
        var saved = await PresentAsync(window => PresentAccountReorder(window, items, pictures));
        if (!saved) return;

        try
        {
            await accountService.SaveAccountLayoutOrderAsync(AccountReorderItem.ToOrder(items));
        }
        catch (Exception exception)
        {
            Serilog.Log.Error(exception, "Could not save the account order.");
            InfoBarMessage(Translator.GeneralTitle_Error, exception.Message, InfoBarMessageType.Error);
        }
    }

    private Task<bool> PresentAccountReorder(NSWindow window, ObservableCollection<AccountReorderItem> items, IPictureStorageService? pictures)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sheet = new NSWindow(new CGRect(0, 0, 440, 480), NSWindowStyle.Titled | NSWindowStyle.Resizable, NSBackingStore.Buffered, false);
        sheet.ReleaseWhenClosed(false);
        sheet.MinSize = new CGSize(360, 320);

        var outline = new AccountReorderOutlineView
        {
            HeaderView = null,
            RowHeight = 40,
            Style = NSTableViewStyle.Inset,
            SelectionHighlightStyle = NSTableViewSelectionHighlightStyle.None,
            IndentationPerLevel = 22,
            FocusRingType = NSFocusRingType.None
        };
        var column = new NSTableColumn("entry") { ResizingMask = NSTableColumnResizing.Autoresizing };
        outline.AddColumn(column);
        outline.OutlineTableColumn = column;
        outline.ColumnAutoresizingStyle = NSTableViewColumnAutoresizingStyle.FirstColumnOnly;
        WinoAccessibility.Label(outline, Translator.SettingsReorderAccounts_Title);
        var source = new AccountReorderSource(items, pictures) { Outline = outline };
        outline.DataSource = source;
        outline.Delegate = source;
        outline.RegisterForDraggedTypes([AccountReorderSource.DragType]);
        outline.DraggingDestinationFeedbackStyle = NSTableViewDraggingDestinationFeedbackStyle.FeedbackStyleGap;
        outline.ReloadData();
        source.ExpandGroups();
        var scroll = new NSScrollView { DocumentView = outline, HasVerticalScroller = true, BorderType = NSBorderType.BezelBorder, TranslatesAutoresizingMaskIntoConstraints = false };

        void Finish(bool result)
        {
            if (completion.TrySetResult(result)) window.EndSheet(sheet);
        }

        var cancel = new NSButton { Title = Translator.Buttons_Cancel, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\u001b" };
        var save = new NSButton { Title = Translator.Buttons_Save, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\r" };
        cancel.Activated += (_, _) => Finish(false);
        save.Activated += (_, _) => Finish(true);

        var title = WinoStyle.Label(Translator.SettingsReorderAccounts_Title, WinoStyle.Heading);
        var hint = WinoStyle.Label(Translator.AccountReorderDialog_Description, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        var buttons = WinoLayout.HStack(WinoStyle.Space2, WinoLayout.Spacer(), cancel, save);
        var stack = WinoLayout.VStack(WinoStyle.Space3, title, hint, scroll, buttons);
        stack.EdgeInsets = new NSEdgeInsets(18, 20, 16, 20);
        foreach (var view in new NSView[] { hint, scroll, buttons }) view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -40).Active = true;
        scroll.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);
        WinoLayout.Fill(stack, sheet.ContentView!);

#if DEBUG
        AccountReorderDebug.Attach(source, Finish);
#endif
        window.BeginSheet(sheet, _ =>
        {
            completion.TrySetResult(false);
#if DEBUG
            AccountReorderDebug.Detach(source);
#endif
            source.Outline = null;
            outline.DataSource = null;
            outline.Delegate = null;
            source.Dispose();
            sheet.Dispose();
        });
        return completion.Task;
    }

    /// <summary>Groups never collapse here, so the disclosure gutter AppKit reserves before every row is dropped.</summary>
    private sealed class AccountReorderOutlineView : NSOutlineView
    {
        public override CGRect FrameOfOutlineCellAtRow(nint row) => CGRect.Empty;

        public override CGRect GetCellFrame(nint column, nint row)
        {
            var frame = base.GetCellFrame(column, row);
            if (row < 0 || column != 0) return frame;
            var x = Math.Min(frame.X, (nfloat)(LevelForRow(row) * IndentationPerLevel));
            return new CGRect(x, frame.Y, frame.Right - x, frame.Height);
        }
    }

    private sealed class AccountReorderSource(ObservableCollection<AccountReorderItem> items, IPictureStorageService? pictures) : NSOutlineViewDataSource, INSOutlineViewDelegate
    {
        public const string DragType = "com.winomail.account-layout-row";
        private const string CellIdentifier = "entry";
        private readonly Dictionary<AccountReorderItem, Node> _nodes = new(ReferenceEqualityComparer.Instance);

        public NSOutlineView? Outline { get; set; }

        public ObservableCollection<AccountReorderItem> Items => items;

        private sealed class Node(AccountReorderItem item) : NSObject
        {
            public AccountReorderItem Item { get; } = item;
        }

        private Node NodeFor(AccountReorderItem item)
        {
            if (!_nodes.TryGetValue(item, out var node)) _nodes[item] = node = new Node(item);
            return node;
        }

        /// <summary>The list that holds <paramref name="item"/> and the group that owns it (null at the top level).</summary>
        public (ObservableCollection<AccountReorderItem> Siblings, AccountReorderItem? Parent) Locate(AccountReorderItem item)
        {
            if (items.Contains(item)) return (items, null);
            foreach (var group in items)
                if (group.Children.Contains(item)) return (group.Children, group);
            return (items, null);
        }

        private ObservableCollection<AccountReorderItem> ChildrenOf(NSObject? item) => item is Node node ? node.Item.Children : items;

        public void ExpandGroups()
        {
            foreach (var group in items.Where(item => item.IsGroup && item.Children.Count > 0))
                Outline?.ExpandItem(NodeFor(group));
        }

        /// <summary>Moves a row among its siblings to <paramref name="target"/> (an index in the list after removal).</summary>
        public bool Move(AccountReorderItem item, int target)
        {
            var (siblings, _) = Locate(item);
            var from = siblings.IndexOf(item);
            target = Math.Clamp(target, 0, siblings.Count - 1);
            if (from < 0 || from == target) return false;
            siblings.Move(from, target);
            Reload();
            return true;
        }

        /// <summary>A drop lands only between siblings of the dragged row.</summary>
        public bool CanDrop(AccountReorderItem dragged, AccountReorderItem? parent, nint index)
            => index >= 0 && ReferenceEquals(Locate(dragged).Parent, parent);

        /// <summary>Applies a drop at <paramref name="index"/> of the parent's list, counted before the dragged row leaves it.</summary>
        public bool Drop(AccountReorderItem dragged, nint index)
        {
            var (siblings, _) = Locate(dragged);
            var from = siblings.IndexOf(dragged);
            var to = (int)index;
            if (to > from) to--;
            return Move(dragged, to);
        }

        private void Reload()
        {
            if (Outline is not { } outline) return;
            outline.ReloadData();
            ExpandGroups();
        }

        private AccountReorderItem? Find(Guid id)
            => items.FirstOrDefault(item => item.Id == id) ?? items.SelectMany(item => item.Children).FirstOrDefault(item => item.Id == id);

        public override nint GetChildrenCount(NSOutlineView outlineView, NSObject? item) => ChildrenOf(item).Count;

        public override NSObject GetChild(NSOutlineView outlineView, nint childIndex, NSObject? item) => NodeFor(ChildrenOf(item)[(int)childIndex]);

        public override bool ItemExpandable(NSOutlineView outlineView, NSObject item) => item is Node { Item: { IsGroup: true, Children.Count: > 0 } };

        [Export("outlineView:shouldCollapseItem:")]
        public bool ShouldCollapseItem(NSOutlineView outlineView, NSObject item) => false;

        [Export("outlineView:shouldShowOutlineCellForItem:")]
        public bool ShouldShowOutlineCell(NSOutlineView outlineView, NSObject item) => false;

        [Export("outlineView:shouldSelectItem:")]
        public bool ShouldSelectItem(NSOutlineView outlineView, NSObject item) => false;

        [Export("outlineView:viewForTableColumn:item:")]
        public NSView GetView(NSOutlineView outlineView, NSTableColumn? tableColumn, NSObject item)
        {
            var cell = outlineView.MakeView(CellIdentifier, this) as AccountReorderCell ?? new AccountReorderCell(this);
            if (item is Node node)
            {
                var (siblings, _) = Locate(node.Item);
                var index = siblings.IndexOf(node.Item);
                cell.Configure(node.Item, pictures, index > 0, index >= 0 && index < siblings.Count - 1);
            }
            return cell;
        }

        [Export("outlineView:pasteboardWriterForItem:")]
        public INSPasteboardWriting? WriterForItem(NSOutlineView outlineView, NSObject item)
        {
            if (item is not Node node) return null;
            var writer = new NSPasteboardItem();
            writer.SetStringForType(node.Item.Id.ToString(), DragType);
            return writer;
        }

        private AccountReorderItem? Dragged(INSDraggingInfo info)
            => Guid.TryParse(info.DraggingPasteboard.GetStringForType(DragType), out var id) ? Find(id) : null;

        public override NSDragOperation ValidateDrop(NSOutlineView outlineView, INSDraggingInfo info, NSObject? item, nint index)
            => Dragged(info) is { } dragged && CanDrop(dragged, (item as Node)?.Item, index) ? NSDragOperation.Move : NSDragOperation.None;

        public override bool AcceptDrop(NSOutlineView outlineView, INSDraggingInfo info, NSObject? item, nint index)
            => Dragged(info) is { } dragged && CanDrop(dragged, (item as Node)?.Item, index) && Drop(dragged, index);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var node in _nodes.Values) node.Dispose();
                _nodes.Clear();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// One reorder row: drag handle, the group icon in its color or the account picture, the name (semibold in the
    /// group color for a group) over the address or account count, then Move up and Move down.
    /// </summary>
    private sealed class AccountReorderCell : NSTableCellView
    {
        private readonly AccountReorderSource _source;
        private readonly WinoIconView _groupIcon = new(WinoIconGlyph.PeopleLink, 18) { Colorful = false };
        private readonly WinoAccountIconView _picture = new(22);
        private readonly NSTextField _title = WinoStyle.Label(string.Empty, WinoStyle.Body);
        private readonly NSTextField _subtitle = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
        private readonly NSButton _up;
        private readonly NSButton _down;
        private AccountReorderItem? _item;

        public AccountReorderItem? Item => _item;
        public NSButton UpButton => _up;
        public NSButton DownButton => _down;

        public AccountReorderCell(AccountReorderSource source)
        {
            _source = source;
            Identifier = "entry";
            var handle = new WinoIconView(WinoIconGlyph.ReOrderDotsVertical, 12, WinoStyle.TertiaryText);
            var leading = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
            WinoLayout.Size(leading, 22, 22);
            foreach (var view in new NSView[] { _groupIcon, _picture })
            {
                leading.AddSubview(view);
                NSLayoutConstraint.ActivateConstraints([view.CenterXAnchor.ConstraintEqualTo(leading.CenterXAnchor), view.CenterYAnchor.ConstraintEqualTo(leading.CenterYAnchor)]);
            }
            foreach (var label in new[] { _title, _subtitle }) label.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
            var text = WinoLayout.VStack(0, _title, _subtitle);
            text.Alignment = NSLayoutAttribute.Leading;
            text.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);
            text.SetClippingResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
            _up = MoveButton(WinoIconGlyph.ChevronUp, Translator.MailFilters_MoveUp);
            _down = MoveButton(WinoIconGlyph.ChevronDown, Translator.MailFilters_MoveDown);
            _up.OnActivated(this, static cell => cell.Move(-1));
            _down.OnActivated(this, static cell => cell.Move(1));
            var row = WinoLayout.HStack(8, handle, leading, text, _up, _down);
            row.Alignment = NSLayoutAttribute.CenterY;
            row.SetCustomSpacing(2, _up);
            WinoLayout.Fill(row, this, 0, 4, 0, 6);
            TextField = _title;
        }

        private static NSButton MoveButton(WinoIconGlyph glyph, string label)
        {
            var button = new NSButton
            {
                Image = WinoIcons.Image(glyph, 11, null, label),
                BezelStyle = NSBezelStyle.Rounded,
                ControlSize = NSControlSize.Small,
                ToolTip = label,
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            WinoAccessibility.Label(button, label);
            return button;
        }

        public void Configure(AccountReorderItem item, IPictureStorageService? pictures, bool canMoveUp, bool canMoveDown)
        {
            _item = item;
            _title.StringValue = item.Title;
            _subtitle.StringValue = item.Subtitle;
            _subtitle.Hidden = string.IsNullOrEmpty(item.Subtitle);
            _groupIcon.Hidden = !item.IsGroup;
            _picture.Hidden = item.IsGroup;
            if (item.IsGroup)
            {
                var tint = WinoStyle.AccountGroupColor(item.ColorKey);
                _groupIcon.Icon = item.IconGlyph;
                _groupIcon.Tint = tint;
                _title.Font = WinoStyle.BodyStrong;
                _title.TextColor = tint ?? WinoStyle.PrimaryText;
                _picture.Account = null;
            }
            else
            {
                _title.Font = WinoStyle.Body;
                _title.TextColor = WinoStyle.PrimaryText;
                _picture.Account = item.Account is { } account && pictures is not null ? MailAccountIconInfoFactory.Create(account, pictures) : null;
            }
            _up.Enabled = canMoveUp;
            _down.Enabled = canMoveDown;
            AccessibilityLabel = item.IsGroup ? item.GroupEntriesAutomationName : item.Title;
        }

        private void Move(int delta)
        {
            if (_item is not { } item) return;
            var (siblings, _) = _source.Locate(item);
            var index = siblings.IndexOf(item);
            if (index < 0) return;
            _source.Move(item, index + delta);
        }
    }

#if DEBUG
    /// <summary>
    /// Debug bridge for the open reorder sheet: <c>reorder-state</c> lists the rows as paths ("1" top level,
    /// "1.0" first account of group 1); <c>reorder-move PATH DELTA</c> presses the row's up or down button;
    /// <c>reorder-drop PATH PARENT INDEX</c> runs the drop validation and drop (PARENT "-" for the top level);
    /// <c>reorder-save</c> and <c>reorder-cancel</c> close the sheet.
    /// </summary>
    private static class AccountReorderDebug
    {
        private static AccountReorderSource? _source;
        private static Action<bool>? _finish;
        private static bool _registered;

        public static void Attach(AccountReorderSource source, Action<bool> finish)
        {
            _source = source;
            _finish = finish;
            if (_registered) return;
            _registered = true;
            MacDebugBridge.Register("reorder-state", _ => Task.FromResult(_source is { } s ? Describe(s) : "no sheet"));
            MacDebugBridge.Register("reorder-move", args =>
            {
                if (_source is not { } s || args.Length < 2 || Find(s, args[0]) is not { } item || !int.TryParse(args[1], out var delta)) return Task.FromResult("usage: reorder-move PATH DELTA");
                if (s.Outline is not { } outline) return Task.FromResult("no outline");
                var cell = Enumerable.Range(0, (int)outline.RowCount).Select(r => outline.GetView(0, r, true)).OfType<AccountReorderCell>().FirstOrDefault(c => ReferenceEquals(c.Item, item));
                if (cell is null) return Task.FromResult("row not found");
                var button = delta < 0 ? cell.UpButton : cell.DownButton;
                if (!button.Enabled) return Task.FromResult("disabled");
                button.PerformClick(button);
                return Task.FromResult(Describe(s));
            });
            MacDebugBridge.Register("reorder-drop", args =>
            {
                if (_source is not { } s || args.Length < 3 || Find(s, args[0]) is not { } item || !int.TryParse(args[2], out var index)) return Task.FromResult("usage: reorder-drop PATH PARENT INDEX");
                var parent = args[1] == "-" ? null : Find(s, args[1]);
                if (!s.CanDrop(item, parent, index)) return Task.FromResult("refused");
                return Task.FromResult((s.Drop(item, index) ? "moved " : "unchanged ") + Describe(s));
            });
            MacDebugBridge.Register("reorder-save", _ => { _finish?.Invoke(true); return Task.FromResult("saved"); });
            MacDebugBridge.Register("reorder-cancel", _ => { _finish?.Invoke(false); return Task.FromResult("cancelled"); });
        }

        public static void Detach(AccountReorderSource source)
        {
            if (!ReferenceEquals(_source, source)) return;
            _source = null;
            _finish = null;
        }

        private static AccountReorderItem? Find(AccountReorderSource source, string path)
        {
            var parts = path.Split('.');
            if (!int.TryParse(parts[0], out var top) || top < 0 || top >= source.Items.Count) return null;
            var item = source.Items[top];
            if (parts.Length < 2) return item;
            return int.TryParse(parts[1], out var child) && child >= 0 && child < item.Children.Count ? item.Children[child] : null;
        }

        private static string Describe(AccountReorderSource source)
            => string.Join(" | ", source.Items.Select((item, index) => item.IsGroup
                ? $"{index}:[{item.Title}] " + string.Join(", ", item.Children.Select((child, childIndex) => $"{index}.{childIndex}:{child.Title}"))
                : $"{index}:{item.Title}"));
    }
#endif
}
