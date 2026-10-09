using AppKit;
using Foundation;
using ObjCRuntime;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail.Compose;

/// <summary>
/// Find (and replace) bar of the composer (design board ComposeFindBar): an NSTextFinder-style strip
/// between the header and the editor. Row one: search field with the options menu (Ignore Case,
/// Whole Words, Replace…), match counter, previous/next segments and Done. Row two (replace mode):
/// replacement field, Replace and Replace All. The bar raises events; the controller drives the editor.
/// </summary>
internal sealed class ComposeFindBar : NSView
{
    private readonly NSSearchField _search;
    private readonly NSTextField _counter;
    private readonly NSSegmentedControl _steps;
    private readonly NSTextField _replace;
    private readonly NSView _replaceRow;
    private readonly NSButton _replaceButton;
    private readonly NSButton _replaceAllButton;
    private bool _ignoreCase = true;
    private bool _wholeWords;

    public event EventHandler? QueryChanged;
    public event EventHandler? NextRequested;
    public event EventHandler? PreviousRequested;
    public event EventHandler? ReplaceRequested;
    public event EventHandler? ReplaceAllRequested;
    public event EventHandler? CloseRequested;

    public ComposeFindBar()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.GroupRole;
        WinoAccessibility.Label(this, Translator.Composer_FindBarLabel);

        _search = new NSSearchField { PlaceholderString = Translator.MailOperation_Find, SendsSearchStringImmediately = true, TranslatesAutoresizingMaskIntoConstraints = false };
        _search.Changed += (_, _) => QueryChanged?.Invoke(this, EventArgs.Empty);
        _search.DoCommandBySelector = FieldCommand;
        WinoLayout.Size(_search, 230, 24);
        WinoAccessibility.Label(_search, Translator.MailOperation_Find);
        UpdateOptionsMenu();

        _counter = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
        _counter.SetContentCompressionResistancePriority(750, NSLayoutConstraintOrientation.Horizontal);
        _counter.AccessibilityRole = NSAccessibilityRoles.StaticTextRole;

        _steps = NSSegmentedControl.FromImages(
            [WinoIcons.Image(WinoIconGlyph.ChevronUp, 12, null, Translator.Composer_FindPrevious), WinoIcons.Image(WinoIconGlyph.ChevronDown, 12, null, Translator.Composer_FindNext)],
            NSSegmentSwitchTracking.Momentary,
            () =>
            {
                if (_steps!.SelectedSegment == 0) PreviousRequested?.Invoke(this, EventArgs.Empty);
                else NextRequested?.Invoke(this, EventArgs.Empty);
            });
        _steps.ControlSize = NSControlSize.Small;
        _steps.TranslatesAutoresizingMaskIntoConstraints = false;
        _steps.SetWidth(30, 0);
        _steps.SetWidth(30, 1);
        _steps.SetToolTip(Translator.Composer_FindPrevious, 0);
        _steps.SetToolTip(Translator.Composer_FindNext, 1);
        WinoAccessibility.Label(_steps, $"{Translator.Composer_FindPrevious}, {Translator.Composer_FindNext}");

        var done = new NSButton { Title = Translator.Buttons_Done, BezelStyle = NSBezelStyle.Push, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false };
        done.Activated += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        WinoAccessibility.Label(done, Translator.Buttons_Done);

        _replace = new NSTextField { PlaceholderString = Translator.Composer_Replace, TranslatesAutoresizingMaskIntoConstraints = false, Font = WinoStyle.Body };
        _replace.DoCommandBySelector = ReplaceFieldCommand;
        WinoLayout.Size(_replace, 230, 24);
        WinoAccessibility.Label(_replace, Translator.Composer_Replace);
        _replaceButton = new NSButton { Title = Translator.Composer_Replace, BezelStyle = NSBezelStyle.Push, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false };
        _replaceButton.Activated += (_, _) => ReplaceRequested?.Invoke(this, EventArgs.Empty);
        WinoAccessibility.Label(_replaceButton, Translator.Composer_Replace);
        _replaceAllButton = new NSButton { Title = Translator.Composer_ReplaceAll, BezelStyle = NSBezelStyle.Push, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false, ToolTip = Translator.Composer_ReplaceAll };
        _replaceAllButton.Activated += (_, _) => ReplaceAllRequested?.Invoke(this, EventArgs.Empty);
        WinoAccessibility.Label(_replaceAllButton, Translator.Composer_ReplaceAll);

        var findRow = WinoLayout.HStack(8, _search, _counter, WinoLayout.Spacer(), _steps, done);
        _replaceRow = WinoLayout.HStack(8, _replace, _replaceButton, _replaceAllButton, WinoLayout.Spacer());
        _replaceRow.Hidden = true;
        var rows = WinoLayout.VStack(6, findRow, _replaceRow);
        rows.EdgeInsets = new NSEdgeInsets(6, 10, 6, 10);
        findRow.WidthAnchor.ConstraintEqualTo(rows.WidthAnchor, 1, -20).Active = true;
        _replaceRow.WidthAnchor.ConstraintEqualTo(rows.WidthAnchor, 1, -20).Active = true;
        findRow.HeightAnchor.ConstraintEqualTo(24).Active = true;
        _replaceRow.HeightAnchor.ConstraintEqualTo(24).Active = true;

        var separator = new WinoSeparator();
        var stack = WinoLayout.VStack(0, rows, separator);
        rows.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        separator.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        WinoLayout.Fill(stack, this);
        SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Vertical);
    }

    public string Query => _search.StringValue ?? string.Empty;
    public string Replacement => _replace.StringValue ?? string.Empty;
    public bool IgnoreCase => _ignoreCase;
    public bool WholeWords => _wholeWords;
    public bool IsReplaceVisible => !_replaceRow.Hidden;

    public override bool IsFlipped => true;
    public override bool WantsUpdateLayer => true;
    public override void UpdateLayer() => Layer!.BackgroundColor = NSColor.WindowBackground.CGColor;

    public void SetQuery(string query) => _search.StringValue = query ?? string.Empty;

    public void SetReplaceVisible(bool visible)
    {
        _replaceRow.Hidden = !visible;
        UpdateOptionsMenu();
    }

    public void FocusSearch()
    {
        Window?.MakeFirstResponder(_search);
        _search.CurrentEditor?.SelectAll(this);
    }

    public bool ContainsFirstResponder(NSResponder? responder)
        => responder is NSView view && view.IsDescendantOf(this);

    /// <summary>Shows "n of m", "Not found" or nothing for an empty query, and enables the actions that apply.</summary>
    public void SetResult(int index, int count)
    {
        var hasQuery = Query.Length > 0;
        _counter.StringValue = !hasQuery ? string.Empty
            : count == 0 ? Translator.Composer_FindNoMatches
            : string.Format(Translator.Composer_FindMatchCount, index, count);
        _counter.TextColor = hasQuery && count == 0 ? NSColor.SystemRed : WinoStyle.SecondaryText;
        _steps.Enabled = count > 0;
        _replaceButton.Enabled = count > 0;
        _replaceAllButton.Enabled = count > 0;
    }

    public string CounterText => _counter.StringValue;

    private void UpdateOptionsMenu()
    {
        var menu = new NSMenu(Translator.MailOperation_Find) { AutoEnablesItems = false };
        menu.AddItem(new NSMenuItem(Translator.Composer_FindIgnoreCase, (_, _) => { _ignoreCase = !_ignoreCase; UpdateOptionsMenu(); QueryChanged?.Invoke(this, EventArgs.Empty); })
        { State = _ignoreCase ? NSCellStateValue.On : NSCellStateValue.Off });
        menu.AddItem(new NSMenuItem(Translator.Composer_FindWholeWords, (_, _) => { _wholeWords = !_wholeWords; UpdateOptionsMenu(); QueryChanged?.Invoke(this, EventArgs.Empty); })
        { State = _wholeWords ? NSCellStateValue.On : NSCellStateValue.Off });
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(new NSMenuItem(Translator.Composer_FindShowReplace, (_, _) => { SetReplaceVisible(_replaceRow?.Hidden != false); if (IsReplaceVisible) Window?.MakeFirstResponder(_replace); })
        { State = _replaceRow is { Hidden: false } ? NSCellStateValue.On : NSCellStateValue.Off });
        _search.SearchMenuTemplate = menu;
    }

    // Return = next, Shift+Return = previous, Esc = close. An input method's marked text never
    // reaches here: AppKit only sends these commands for committed keys.
    private bool FieldCommand(NSControl control, NSTextView textView, Selector selector)
    {
        switch (selector.Name)
        {
            case "insertNewline:":
                var shift = (NSApplication.SharedApplication.CurrentEvent?.ModifierFlags ?? 0).HasFlag(NSEventModifierMask.ShiftKeyMask);
                (shift ? PreviousRequested : NextRequested)?.Invoke(this, EventArgs.Empty);
                return true;
            case "insertTab:" when IsReplaceVisible:
                Window?.MakeFirstResponder(_replace);
                return true;
            case "cancelOperation:":
                CloseRequested?.Invoke(this, EventArgs.Empty);
                return true;
        }
        return false;
    }

    private bool ReplaceFieldCommand(NSControl control, NSTextView textView, Selector selector)
    {
        switch (selector.Name)
        {
            case "insertNewline:":
                ReplaceRequested?.Invoke(this, EventArgs.Empty);
                return true;
            case "insertBacktab:":
                Window?.MakeFirstResponder(_search);
                return true;
            case "cancelOperation:":
                CloseRequested?.Invoke(this, EventArgs.Empty);
                return true;
        }
        return false;
    }
}
