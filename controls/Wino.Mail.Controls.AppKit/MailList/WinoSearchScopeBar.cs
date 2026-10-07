using AppKit;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.MailList;

/// <summary>A filter token in the scope bar. Active tokens are tinted; removable tokens show an x.</summary>
public sealed record WinoSearchChip(object Tag, string Text, string? Prefix = null, bool IsActive = false, bool IsRemovable = false);

/// <summary>
/// Search scope bar shown above the mail list while a search is active (design board "Search"):
/// a Local/Online segmented control, a folder scope pull-down, a status line and Done on the
/// first line; wrapping filter tokens below. Value tokens carry a bold prefix, boolean filters toggle.
/// </summary>
public sealed class WinoSearchScopeBar : WinoSurfaceView
{
    private readonly NSSegmentedControl _reach;
    private readonly NSPopUpButton _scope;
    private readonly NSTextField _status;
    private readonly NSButton _done;
    private readonly WinoFlowView _chips;
    private bool _updating;

    public WinoSearchScopeBar(string localTitle, string onlineTitle, string doneTitle)
    {
        Fill = NSColor.WindowBackground;
        _reach = NSSegmentedControl.FromLabels([localTitle, onlineTitle], NSSegmentSwitchTracking.SelectOne, () =>
        {
            if (!_updating) ReachChanged?.Invoke(this, EventArgs.Empty);
        });
        _reach.ControlSize = NSControlSize.Small;
        _reach.SelectedSegment = 0;
        _reach.TranslatesAutoresizingMaskIntoConstraints = false;

        _scope = new NSPopUpButton { PullsDown = false, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false };
        _scope.Font = NSFont.SystemFontOfSize(11);
        _scope.Activated += (_, _) => { if (!_updating) ScopeChanged?.Invoke(this, (int)_scope.IndexOfSelectedItem); };

        _status = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
        _status.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        _status.SetContentCompressionResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);

        _done = new NSButton { Title = doneTitle, Bordered = false, BezelStyle = NSBezelStyle.Inline, TranslatesAutoresizingMaskIntoConstraints = false };
        _done.AttributedTitle = new Foundation.NSAttributedString(doneTitle, new NSStringAttributes { Font = NSFont.SystemFontOfSize(12, NSFontWeight.Medium), ForegroundColor = WinoStyle.Accent });
        _done.Activated += (_, _) => DoneClicked?.Invoke(this, EventArgs.Empty);
        _done.SetContentHuggingPriorityForOrientation(1000, NSLayoutConstraintOrientation.Horizontal);

        var line = WinoLayout.HStack(8, _reach, _scope, _status, _done);
        _chips = new WinoFlowView();
        var stack = WinoLayout.VStack(8, line, _chips);
        line.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        _chips.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        stack.EdgeInsets = new NSEdgeInsets(10, 12, 10, 12);
        WinoLayout.Fill(stack, this);

        var separator = new WinoSeparator();
        AddSubview(separator);
        NSLayoutConstraint.ActivateConstraints(
        [
            separator.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            separator.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            separator.BottomAnchor.ConstraintEqualTo(BottomAnchor)
        ]);

        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.ToolbarRole;
    }

    public event EventHandler? ReachChanged;
    public event EventHandler<int>? ScopeChanged;
    public event EventHandler? DoneClicked;
    public event EventHandler<WinoSearchChip>? ChipClicked;

    public bool IsOnline
    {
        get => _reach.SelectedSegment == 1;
        set { _updating = true; _reach.SelectedSegment = value ? 1 : 0; _updating = false; }
    }

    public void SetScopes(IReadOnlyList<string> titles, int selectedIndex)
    {
        _updating = true;
        _scope.RemoveAllItems();
        foreach (var title in titles) _scope.AddItem(title);
        if (selectedIndex >= 0 && selectedIndex < titles.Count) _scope.SelectItem(selectedIndex);
        _updating = false;
    }

    public string Status
    {
        get => _status.StringValue;
        set => _status.StringValue = value ?? string.Empty;
    }

    public void SetChips(IEnumerable<WinoSearchChip> chips)
    {
        var views = new List<NSView>();
        foreach (var spec in chips)
        {
            var chip = new WinoChipView(22)
            {
                Text = spec.Text,
                Prefix = spec.Prefix,
                Tag = spec,
                IsClickable = true,
                Fill = spec.IsActive ? WinoStyle.Accent.ColorWithAlphaComponent((nfloat)0.14) : NSColor.ControlBackground,
                Stroke = spec.IsActive ? null : NSColor.Separator,
                TextColor = spec.IsActive ? WinoStyle.PrimaryText : WinoStyle.SecondaryText
            };
            if (spec.IsRemovable) chip.SetGlyph(Wino.Core.Domain.Enums.WinoIconGlyph.Dismiss, 8);
            chip.AccessibilityRole = NSAccessibilityRoles.CheckBoxRole;
            chip.AccessibilityValue = Foundation.NSNumber.FromBoolean(spec.IsActive);
            chip.Clicked += (_, _) => ChipClicked?.Invoke(this, spec);
            views.Add(chip);
        }
        var previous = _chips.Subviews;
        _chips.SetItems(views);
        foreach (var old in previous) old.Dispose();
        _chips.Hidden = views.Count == 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReachChanged = null;
            ScopeChanged = null;
            DoneClicked = null;
            ChipClicked = null;
        }
        base.Dispose(disposing);
    }
}
