using AppKit;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail.Compose;

/// <summary>
/// Recipient suggestions under a To/Cc/Bcc field (Windows RecipientSuggestionTemplate), drawn the way Mail
/// shows address completion: a borderless child panel with picture, name (matched text in semibold),
/// address or member count, the source caption and, for remembered correspondents, a "Don't suggest" ✕
/// that keeps the list open. The panel never becomes key, so the caret stays in the field; the composer
/// forwards ↑ ↓ ↩ ⇥ and Esc. Main thread only.
/// </summary>
internal sealed class ComposeRecipientSuggestionPopup : NSObject
{
    private const double RowHeight = 44;
    private const int MaximumRows = 8;
    private readonly IPictureStorageService _pictures;
    private readonly Dictionary<Guid, NSImage?> _pictureCache = [];
    private readonly SuggestionPanel _panel;
    private readonly SuggestionTable _table;
    private readonly SuggestionSource _source;
    private readonly SuggestionDelegate _delegate;
    private readonly NSScrollView _scroll;
    private List<RecipientSuggestion> _items = [];
    private string _query = string.Empty;
    private NSView? _anchor;
    private NSWindow? _parent;
    private bool _disposed;

    public ComposeRecipientSuggestionPopup(IPictureStorageService pictures)
    {
        _pictures = pictures;
        _table = new SuggestionTable(this)
        {
            HeaderView = null,
            RowHeight = (nfloat)RowHeight,
            IntercellSpacing = new CGSize(0, 0),
            Style = NSTableViewStyle.Inset,
            AllowsEmptySelection = true,
            AllowsMultipleSelection = false,
            BackgroundColor = NSColor.Clear,
            RefusesFirstResponder = true
        };
        _table.AddColumn(new NSTableColumn("suggestion") { ResizingMask = NSTableColumnResizing.Autoresizing });
        _source = new SuggestionSource(this);
        _delegate = new SuggestionDelegate(this);
        _table.DataSource = _source;
        _table.Delegate = _delegate;
        _table.Target = this;
        _table.Action = new Selector("rowClicked:");

        _scroll = new NSScrollView { DocumentView = _table, HasVerticalScroller = true, AutohidesScrollers = true, DrawsBackground = false, TranslatesAutoresizingMaskIntoConstraints = false };
        var effect = new NSVisualEffectView
        {
            Material = NSVisualEffectMaterial.Menu,
            BlendingMode = NSVisualEffectBlendingMode.BehindWindow,
            State = NSVisualEffectState.Active,
            WantsLayer = true
        };
        effect.Layer!.CornerRadius = 8;
        effect.Layer.MasksToBounds = true;
        WinoLayout.Fill(_scroll, effect, 4);

        _panel = new SuggestionPanel(new CGRect(0, 0, 320, RowHeight), NSWindowStyle.Borderless | NSWindowStyle.NonactivatingPanel, NSBackingStore.Buffered, false)
        {
            ContentView = effect,
            BackgroundColor = NSColor.Clear,
            IsOpaque = false,
            HasShadow = true,
            HidesOnDeactivate = true,
            BecomesKeyOnlyIfNeeded = true
        };
        _panel.ReleaseWhenClosed(false);
        _panel.AccessibilityRole = NSAccessibilityRoles.ListRole;
    }

    /// <summary>A row was chosen with the mouse.</summary>
    public event EventHandler<RecipientSuggestion>? Picked;

    /// <summary>The "Don't suggest" button of a remembered correspondent.</summary>
    public event EventHandler<RecipientSuggestion>? SuppressRequested;

    public bool IsVisible => !_disposed && _panel.IsVisible;

    public IReadOnlyList<RecipientSuggestion> Items => _items;

    public string Query => _query;

    public NSView? Anchor => _anchor;

    public RecipientSuggestion? Highlighted
        => _table.SelectedRow >= 0 && _table.SelectedRow < _items.Count ? _items[(int)_table.SelectedRow] : null;

    /// <summary>Shows <paramref name="items"/> under <paramref name="anchor"/>, the first row highlighted; empty closes.</summary>
    public void Show(NSView anchor, IReadOnlyList<RecipientSuggestion> items, string query)
    {
        if (_disposed) return;
        if (items.Count == 0 || anchor.Window is not { } parent)
        {
            Close();
            return;
        }
        _items = items.ToList();
        _query = query;
        _anchor = anchor;
        _table.ReloadData();
        _table.SelectRow(0, false);
        _table.ScrollRowToVisible(0);
        WinoAccessibility.Label(_panel.ContentView!, anchor.AccessibilityLabel);
        if (_parent != parent)
        {
            if (_parent is not null) _parent.RemoveChildWindow(_panel);
            _parent = parent;
            parent.AddChildWindow(_panel, NSWindowOrderingMode.Above);
        }
        Reposition();
        _panel.OrderFront(null);
        AnnounceSelection();
    }

    /// <summary>Keeps the panel under the field after the field moved or resized.</summary>
    public void Reposition()
    {
        if (_disposed || _anchor?.Window is not { } window) return;
        var rows = Math.Min(_items.Count, MaximumRows);
        var height = rows * RowHeight + 8 + 12;
        var inWindow = _anchor.ConvertRectToView(_anchor.Bounds, null);
        var onScreen = window.ConvertRectToScreen(inWindow);
        var width = Math.Max(onScreen.Width, 320);
        _panel.SetFrame(new CGRect(onScreen.X, onScreen.Y - height - 2, width, height), true);
    }

    /// <summary>Takes a row out without closing the list (after "Don't suggest").</summary>
    public void Remove(RecipientSuggestion suggestion)
    {
        var index = _items.FindIndex(item => ReferenceEquals(item, suggestion));
        if (index < 0) return;
        var selected = (int)_table.SelectedRow;
        _items.RemoveAt(index);
        if (_items.Count == 0)
        {
            Close();
            return;
        }
        _table.ReloadData();
        _table.SelectRow(Math.Clamp(selected >= index ? selected - (selected > index ? 1 : 0) : selected, 0, _items.Count - 1), false);
        Reposition();
    }

    /// <summary>Arrow keys: the keyboard highlight always wins over the last hover position.</summary>
    public void MoveSelection(int delta)
    {
        if (!IsVisible || _items.Count == 0) return;
        var row = (int)_table.SelectedRow;
        row = row < 0 ? (delta > 0 ? 0 : _items.Count - 1) : Math.Clamp(row + delta, 0, _items.Count - 1);
        _table.SelectRow(row, false);
        _table.ScrollRowToVisible(row);
        AnnounceSelection();
    }

    public void Close()
    {
        if (_disposed) return;
        _panel.OrderOut(null);
        if (_parent is not null)
        {
            _parent.RemoveChildWindow(_panel);
            _parent = null;
        }
        _items = [];
        _query = string.Empty;
        _anchor = null;
        _table.ReloadData();
    }

    private void AnnounceSelection()
    {
        if (Highlighted is { } suggestion)
            NSAccessibility.PostNotification(_table, new NSString("AXSelectedRowsChanged"));
    }

    [Export("rowClicked:")]
    private void RowClicked(NSObject sender)
    {
        var row = (int)_table.ClickedRow;
        if (row >= 0 && row < _items.Count) Picked?.Invoke(this, _items[row]);
    }

    private void HoverRow(int row)
    {
        if (row >= 0 && row < _items.Count && row != _table.SelectedRow) _table.SelectRow(row, false);
    }

    private NSImage? Picture(RecipientSuggestion suggestion)
    {
        if (suggestion.ContactPictureFileId is not { } id) return null;
        if (_pictureCache.TryGetValue(id, out var cached)) return cached;
        NSImage? image = null;
        try
        {
            var path = _pictures.GetPicturePath(PictureKind.Contact, id);
            if (File.Exists(path)) image = new NSImage(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        _pictureCache[id] = image;
        return image;
    }

    /// <summary>The name with the typed text in semibold (Windows TextMatchHighlighter).</summary>
    internal static NSAttributedString Highlight(string text, string query, NSFont font, NSFont strong, NSColor color)
    {
        var attributed = new NSMutableAttributedString(text, new NSStringAttributes { Font = font, ForegroundColor = color }.Dictionary);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var index = text.IndexOf(query.Trim(), StringComparison.CurrentCultureIgnoreCase);
            if (index >= 0) attributed.AddAttribute(NSStringAttributeKey.Font, strong, new NSRange(index, query.Trim().Length));
        }
        return attributed;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            Close();
            _disposed = true;
            Picked = null;
            SuppressRequested = null;
            _table.Target = null!;
            _table.DataSource = null!;
            _table.Delegate = null!;
            _source.Dispose();
            _delegate.Dispose();
            _panel.Close();
            _panel.Dispose();
            _pictureCache.Clear();
        }
        base.Dispose(disposing);
    }

    // ---- Panel and table ----

    private sealed class SuggestionPanel(CGRect rect, NSWindowStyle style, NSBackingStore backing, bool defer) : NSPanel(rect, style, backing, defer)
    {
        public override bool CanBecomeKeyWindow => false;
        public override bool CanBecomeMainWindow => false;
    }

    /// <summary>Hover follows real pointer movement only, so a list that changes under a still pointer keeps the keyboard row.</summary>
    private sealed class SuggestionTable(ComposeRecipientSuggestionPopup owner) : NSTableView
    {
        private NSTrackingArea? _tracking;

        public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;

        public override void UpdateTrackingAreas()
        {
            base.UpdateTrackingAreas();
            if (_tracking is not null) RemoveTrackingArea(_tracking);
            _tracking = new NSTrackingArea(Bounds, NSTrackingAreaOptions.MouseMoved | NSTrackingAreaOptions.ActiveAlways | NSTrackingAreaOptions.InVisibleRect, this, null);
            AddTrackingArea(_tracking);
        }

        public override void MouseMoved(NSEvent theEvent)
        {
            base.MouseMoved(theEvent);
            owner.HoverRow((int)GetRow(ConvertPointFromView(theEvent.LocationInWindow, null)));
        }
    }

    private sealed class SuggestionSource(ComposeRecipientSuggestionPopup owner) : NSTableViewDataSource
    {
        public override nint GetRowCount(NSTableView tableView) => owner._items.Count;
    }

    private sealed class SuggestionDelegate(ComposeRecipientSuggestionPopup owner) : NSTableViewDelegate
    {
        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn tableColumn, nint row)
        {
            var suggestion = owner._items[(int)row];
            var cell = tableView.MakeView(SuggestionCell.CellIdentifier, this) as SuggestionCell ?? new SuggestionCell(owner);
            cell.Show(suggestion, owner._query, owner.Picture(suggestion));
            return cell;
        }

        public override bool ShouldSelectRow(NSTableView tableView, nint row) => true;
    }

    private sealed class SuggestionCell : NSTableCellView
    {
        public const string CellIdentifier = "RecipientSuggestionCell";
        private readonly ComposeRecipientSuggestionPopup _owner;
        private readonly WinoContactPicture _picture;
        private readonly WinoIconView _listGlyph;
        private readonly NSTextField _name;
        private readonly NSTextField _secondary;
        private readonly NSTextField _source;
        private readonly NSButton _dismiss;
        private RecipientSuggestion? _suggestion;

        public SuggestionCell(ComposeRecipientSuggestionPopup owner)
        {
            _owner = owner;
            Identifier = CellIdentifier;
            _picture = new WinoContactPicture(28);
            _listGlyph = new WinoIconView(WinoIconGlyph.People, 18, WinoStyle.SecondaryText);
            WinoLayout.Size(_listGlyph, 28, 28);
            _name = WinoStyle.Label(string.Empty, WinoStyle.Body);
            _name.LineBreakMode = NSLineBreakMode.TruncatingTail;
            _secondary = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
            _secondary.LineBreakMode = NSLineBreakMode.TruncatingTail;
            foreach (var label in new[] { _name, _secondary }) label.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
            var text = WinoLayout.VStack(1, _name, _secondary);
            text.Alignment = NSLayoutAttribute.Leading;
            text.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
            _source = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.TertiaryText);
            _source.LineBreakMode = NSLineBreakMode.TruncatingTail;
            _source.WidthAnchor.ConstraintLessThanOrEqualTo(140).Active = true;
            _dismiss = new NSButton
            {
                Bordered = false,
                BezelStyle = NSBezelStyle.Inline,
                Title = string.Empty,
                Image = WinoIcons.Image(WinoIconGlyph.Dismiss, 12, null, Translator.RecipientSuggestion_DontSuggest),
                ImagePosition = NSCellImagePosition.ImageOnly,
                ContentTintColor = WinoStyle.TertiaryText,
                ToolTip = Translator.RecipientSuggestion_DontSuggest,
                RefusesFirstResponder = true,
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            WinoLayout.Size(_dismiss, 24, 24);
            WinoAccessibility.Label(_dismiss, Translator.RecipientSuggestion_DontSuggest);
            _dismiss.Activated += (_, _) => { if (_suggestion is { } suggestion) _owner.SuppressRequested?.Invoke(_owner, suggestion); };
            var row = WinoLayout.HStack(10, _picture, _listGlyph, text, WinoLayout.Spacer(), _source, _dismiss);
            row.EdgeInsets = new NSEdgeInsets(0, 6, 0, 4);
            WinoLayout.Fill(row, this);
        }

        public void Show(RecipientSuggestion suggestion, string query, NSImage? picture)
        {
            _suggestion = suggestion;
            var name = string.IsNullOrWhiteSpace(suggestion.DisplayName) ? suggestion.Address ?? string.Empty : suggestion.DisplayName;
            _name.AttributedStringValue = Highlight(name, query, WinoStyle.Body, WinoStyle.BodyStrong, WinoStyle.PrimaryText);
            var secondary = suggestion.SecondaryText ?? string.Empty;
            _secondary.AttributedStringValue = Highlight(secondary, query, WinoStyle.Caption, WinoStyle.CaptionStrong, WinoStyle.SecondaryText);
            _source.StringValue = suggestion.SourceCaption ?? string.Empty;
            _source.Hidden = string.IsNullOrWhiteSpace(suggestion.SourceCaption);
            _picture.Hidden = suggestion.IsList;
            _listGlyph.Hidden = !suggestion.IsList;
            if (!suggestion.IsList)
            {
                _picture.Image = picture;
                _picture.SetIdentity(name, suggestion.Address);
            }
            _dismiss.Hidden = !suggestion.CanSuppress;
            AccessibilityLabel = string.Join(", ", new[] { name, secondary, suggestion.SourceCaption }.Where(static part => !string.IsNullOrWhiteSpace(part)));
        }
    }
}
