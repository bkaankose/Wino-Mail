using AppKit;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail.Compose;

/// <summary>
/// "Insert template" popover (design board ComposeTemplatesSignature): a 320pt list of the stored
/// templates (name + description, 40pt rows), a search field once there are more than eight, and a
/// "Manage templates…" footer. With no templates it shows the empty state with "Create a template".
/// A click or Return inserts the template and closes the popover.
/// </summary>
internal sealed class ComposeTemplatePopover : NSObject
{
    private const int SearchThreshold = 8;
    private readonly NSPopover _popover;
    private readonly IReadOnlyList<EmailTemplate> _templates;
    private readonly Action<EmailTemplate> _picked;
    private readonly Action _manage;
    private readonly Action _create;
    private readonly TemplateTable? _table;
    private readonly TemplateSource? _source;
    private readonly TemplateDelegate? _delegate;
    private readonly NSSearchField? _search;
    private List<EmailTemplate> _visible;
    private bool _closed;

    public ComposeTemplatePopover(IReadOnlyList<EmailTemplate> templates, Action<EmailTemplate> picked, Action manage, Action create)
    {
        _templates = templates;
        _visible = templates.ToList();
        _picked = picked;
        _manage = manage;
        _create = create;

        NSView content;
        CGSize size;
        if (templates.Count == 0)
        {
            content = BuildEmptyState();
            size = new CGSize(260, 250);
        }
        else
        {
            var title = WinoStyle.Label(Translator.Composer_EmailTemplatesPlaceholder, WinoStyle.BodyStrong);
            WinoAccessibility.Label(title, Translator.Composer_EmailTemplatesPlaceholder);
            title.AccessibilityRole = NSAccessibilityRoles.StaticTextRole;

            _table = new TemplateTable(this)
            {
                HeaderView = null,
                RowHeight = 40,
                IntercellSpacing = new CGSize(0, 2),
                Style = NSTableViewStyle.Inset,
                AllowsEmptySelection = true,
                AllowsMultipleSelection = false,
                BackgroundColor = NSColor.Clear
            };
            _table.AddColumn(new NSTableColumn("template") { ResizingMask = NSTableColumnResizing.Autoresizing });
            _source = new TemplateSource(this);
            _delegate = new TemplateDelegate(this);
            _table.DataSource = _source;
            _table.Delegate = _delegate;
            _table.Target = this;
            _table.Action = new Selector("rowClicked:");
            WinoAccessibility.Label(_table, Translator.Composer_EmailTemplatesPlaceholder);

            var scroll = new NSScrollView { DocumentView = _table, HasVerticalScroller = true, AutohidesScrollers = true, DrawsBackground = false, TranslatesAutoresizingMaskIntoConstraints = false };
            var rows = Math.Min(templates.Count, SearchThreshold);
            var listHeight = rows * 42 + 8;
            scroll.HeightAnchor.ConstraintEqualTo(listHeight).Active = true;

            var manageButton = new NSButton
            {
                Title = Translator.Composer_ManageTemplates,
                Bordered = false,
                BezelStyle = NSBezelStyle.Inline,
                ContentTintColor = WinoStyle.Accent,
                Font = WinoStyle.Body,
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            manageButton.Activated += (_, _) => { Close(); _manage(); };
            WinoAccessibility.Label(manageButton, Translator.Composer_ManageTemplates);

            var views = new List<NSView> { title };
            if (templates.Count > SearchThreshold)
            {
                _search = new NSSearchField { PlaceholderString = Translator.Composer_SearchTemplates, SendsSearchStringImmediately = true, TranslatesAutoresizingMaskIntoConstraints = false };
                _search.Changed += (_, _) => ApplyFilter(_search.StringValue);
                _search.DoCommandBySelector = SearchCommand;
                WinoAccessibility.Label(_search, Translator.Composer_SearchTemplates);
                views.Add(_search);
            }
            views.Add(scroll);
            views.Add(new WinoSeparator());
            views.Add(manageButton);

            var stack = WinoLayout.VStack(8, views.ToArray());
            stack.EdgeInsets = new NSEdgeInsets(12, 10, 10, 10);
            foreach (var view in stack.ArrangedSubviews)
                if (view != manageButton && view != title) view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -20).Active = true;
            content = stack;
            size = new CGSize(320, listHeight + (templates.Count > SearchThreshold ? 32 : 0) + 84);
        }

        var root = new NSView(new CGRect(CGPoint.Empty, size));
        WinoLayout.Fill(content, root);
        root.AccessibilityElement = true;
        root.AccessibilityRole = NSAccessibilityRoles.PopoverRole;
        WinoAccessibility.Label(root, Translator.Composer_EmailTemplatesPlaceholder);

        _popover = new NSPopover
        {
            Behavior = NSPopoverBehavior.Transient,
            Animates = true,
            ContentSize = size,
            ContentViewController = new NSViewController { View = root }
        };
        _table?.ReloadData();
    }

    public bool IsShown => _popover.Shown;

    public void Show(NSView anchor)
    {
        _popover.Show(anchor.Bounds, anchor, NSRectEdge.MaxYEdge);
        if (_search is not null) _search.Window?.MakeFirstResponder(_search);
        else if (_table is not null)
        {
            _table.Window?.MakeFirstResponder(_table);
            if (_visible.Count > 0) _table.SelectRow(0, false);
        }
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        if (_popover.Shown) _popover.Close();
    }

    private NSView BuildEmptyState()
    {
        var badge = new WinoSurfaceView { Fill = WinoStyle.Accent.ColorWithAlphaComponent(0.14f), CornerRadius = 10, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(badge, 44, 44);
        var glyph = new WinoIconView(WinoIconGlyph.DocumentAdd, 22, WinoStyle.Accent);
        badge.AddSubview(glyph);
        glyph.CenterXAnchor.ConstraintEqualTo(badge.CenterXAnchor).Active = true;
        glyph.CenterYAnchor.ConstraintEqualTo(badge.CenterYAnchor).Active = true;

        var title = WinoStyle.Label(Translator.SettingsEmailTemplates_EmptyTitle, WinoStyle.BodyStrong);
        title.Alignment = NSTextAlignment.Center;
        var description = WinoStyle.Label(Translator.SettingsEmailTemplates_EmptyDescription, WinoStyle.Caption, WinoStyle.SecondaryText, maximumLines: 4);
        description.Alignment = NSTextAlignment.Center;
        description.PreferredMaxLayoutWidth = 220;
        var create = new NSButton { Title = Translator.SettingsEmailTemplates_EmptyAction, BezelStyle = NSBezelStyle.Push, BezelColor = WinoStyle.Accent, KeyEquivalent = "\r", TranslatesAutoresizingMaskIntoConstraints = false };
        create.Activated += (_, _) => { Close(); _create(); };
        WinoAccessibility.Label(create, Translator.SettingsEmailTemplates_EmptyAction);

        var stack = WinoLayout.VStack(8, badge, title, description, create);
        stack.Alignment = NSLayoutAttribute.CenterX;
        stack.SetCustomSpacing(12, badge);
        stack.SetCustomSpacing(14, description);
        stack.EdgeInsets = new NSEdgeInsets(20, 20, 20, 20);
        description.WidthAnchor.ConstraintEqualTo(220).Active = true;
        return stack;
    }

    private void ApplyFilter(string text)
    {
        text = text?.Trim() ?? string.Empty;
        _visible = text.Length == 0
            ? _templates.ToList()
            : _templates.Where(template => (template.Name ?? string.Empty).Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                                           (template.Description ?? string.Empty).Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToList();
        _table?.ReloadData();
        if (_visible.Count > 0) _table?.SelectRow(0, false);
    }

    private bool SearchCommand(NSControl control, NSTextView textView, Selector selector)
    {
        switch (selector.Name)
        {
            case "insertNewline:":
                PickSelected();
                return true;
            case "moveDown:":
                if (_table is not null && _visible.Count > 0)
                {
                    _table.Window?.MakeFirstResponder(_table);
                    _table.SelectRow(Math.Min(Math.Max(0, (int)_table.SelectedRow + 1), _visible.Count - 1), false);
                }
                return true;
            case "cancelOperation:":
                Close();
                return true;
        }
        return false;
    }

    [Export("rowClicked:")]
    private void RowClicked(NSObject sender)
    {
        if (_table is null) return;
        var row = (int)_table.ClickedRow;
        if (row >= 0 && row < _visible.Count) Pick(_visible[row]);
    }

    private void PickSelected()
    {
        if (_table is null || _visible.Count == 0) return;
        var row = (int)_table.SelectedRow;
        Pick(_visible[row >= 0 && row < _visible.Count ? row : 0]);
    }

    private void Pick(EmailTemplate template)
    {
        Close();
        _picked(template);
    }

    /// <summary>Return inserts the selected template; Esc closes the popover.</summary>
    private sealed class TemplateTable(ComposeTemplatePopover owner) : NSTableView
    {
        public override void KeyDown(NSEvent theEvent)
        {
            if (theEvent.KeyCode is 36 or 76)
            {
                owner.PickSelected();
                return;
            }
            base.KeyDown(theEvent);
        }
    }

    private sealed class TemplateSource(ComposeTemplatePopover owner) : NSTableViewDataSource
    {
        public override nint GetRowCount(NSTableView tableView) => owner._visible.Count;
    }

    private sealed class TemplateDelegate(ComposeTemplatePopover owner) : NSTableViewDelegate
    {
        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn tableColumn, nint row)
        {
            var template = owner._visible[(int)row];
            var icon = new WinoIconView(WinoIconGlyph.Document, 14, WinoStyle.SecondaryText);
            var name = WinoStyle.Label(template.Name, WinoStyle.BodyStrong);
            name.LineBreakMode = NSLineBreakMode.TruncatingTail;
            var hasDescription = !string.IsNullOrWhiteSpace(template.Description);
            var description = WinoStyle.Label(hasDescription ? template.Description : Translator.SettingsEmailTemplates_DescriptionPlaceholder,
                WinoStyle.Caption, hasDescription ? WinoStyle.SecondaryText : WinoStyle.TertiaryText);
            description.LineBreakMode = NSLineBreakMode.TruncatingTail;
            var text = WinoLayout.VStack(1, name, description);
            name.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
            description.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
            var rowStack = WinoLayout.HStack(8, icon, text);
            var cell = new NSTableCellView();
            WinoLayout.Fill(rowStack, cell, 0, 4, 0, 6);
            cell.AccessibilityLabel = hasDescription ? $"{template.Name}, {template.Description}" : template.Name;
            return cell;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
            if (_table is not null)
            {
                _table.DataSource = null!;
                _table.Delegate = null!;
                _table.Target = null!;
            }
            _source?.Dispose();
            _delegate?.Dispose();
        }
        base.Dispose(disposing);
    }
}
