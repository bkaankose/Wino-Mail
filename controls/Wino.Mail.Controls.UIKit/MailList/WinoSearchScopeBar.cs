using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.MailList;

/// <summary>A filter token in the scope bar. Active tokens are tinted; removable tokens show an x.</summary>
public sealed record WinoSearchChip(object Tag, string Text, string? Prefix = null, bool IsActive = false, bool IsRemovable = false);

/// <summary>
/// Search scope bar shown above the mail list while a search is active (UIKit twin of the AppKit
/// <c>WinoSearchScopeBar</c>): a Local/Online segmented control, a folder scope pull-down, a status line and Done on
/// the first line; wrapping filter tokens below. Value tokens carry a bold prefix, boolean filters toggle.
/// </summary>
public sealed class WinoSearchScopeBar : WinoSurfaceView
{
    private readonly UISegmentedControl _reach;
    private readonly UIButton _scope;
    private readonly UILabel _status;
    private readonly UIButton _done;
    private readonly WinoFlowView _chips;
    private IReadOnlyList<string> _scopeTitles = Array.Empty<string>();
    private int _scopeIndex = -1;
    private bool _updating;

    public WinoSearchScopeBar(string localTitle, string onlineTitle, string doneTitle)
    {
        Fill = UIColor.SystemBackground;
        _reach = new UISegmentedControl(new[] { localTitle, onlineTitle }) { SelectedSegment = 0, TranslatesAutoresizingMaskIntoConstraints = false };
        _reach.ValueChanged += (_, _) => { if (!_updating) ReachChanged?.Invoke(this, EventArgs.Empty); };

        var scopeConfiguration = UIButtonConfiguration.GrayButtonConfiguration;
        scopeConfiguration.ButtonSize = UIButtonConfigurationSize.Small;
        scopeConfiguration.TitleLineBreakMode = UILineBreakMode.TailTruncation;
        _scope = new UIButton { Configuration = scopeConfiguration, ShowsMenuAsPrimaryAction = true, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Priorities(_scope, UILayoutConstraintAxis.Horizontal, 250, 300);

        _status = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
        WinoLayout.Priorities(_status, UILayoutConstraintAxis.Horizontal, 1, 200);

        var doneConfiguration = UIButtonConfiguration.PlainButtonConfiguration;
        doneConfiguration.Title = doneTitle;
        doneConfiguration.BaseForegroundColor = WinoStyle.Accent;
        _done = new UIButton { Configuration = doneConfiguration, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Priorities(_done, UILayoutConstraintAxis.Horizontal, 1000, 1000);
        _done.PrimaryActionTriggered += (_, _) => DoneClicked?.Invoke(this, EventArgs.Empty);

        var line = WinoLayout.HStack(8, _reach, _scope, _status, _done);
        _chips = new WinoFlowView();
        var stack = WinoLayout.Insets(WinoLayout.VStack(8, line, _chips), 10, 12, 10, 12);
        stack.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(stack, this);

        var separator = new WinoSeparator();
        AddSubview(separator);
        NSLayoutConstraint.ActivateConstraints(
        [
            separator.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            separator.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            separator.BottomAnchor.ConstraintEqualTo(BottomAnchor)
        ]);

        ShouldGroupAccessibilityChildren = true;
        WinoStyle.AccentWatchers.Add(this, static view => view.AccentChanged(null, EventArgs.Empty));
    }

    public event EventHandler? ReachChanged;
    public event EventHandler<int>? ScopeChanged;
    public event EventHandler? DoneClicked;
    public event EventHandler<WinoSearchChip>? ChipClicked;

    /// <summary>The trailing Filters token was tapped; the sender is the token, for anchoring a popover.</summary>
    public event EventHandler? FiltersClicked;

    /// <summary>Title of a trailing Filters token after the filter tokens; null leaves it out.</summary>
    public string? FiltersTitle { get; set; }

    /// <summary>Active filters shown on the Filters token; above zero the token is tinted.</summary>
    public int ActiveFilterCount { get; set; }

    public bool IsOnline
    {
        get => _reach.SelectedSegment == 1;
        set { _updating = true; _reach.SelectedSegment = value ? 1 : 0; _updating = false; }
    }

    public void SetScopes(IReadOnlyList<string> titles, int selectedIndex)
    {
        _scopeTitles = titles;
        _scopeIndex = selectedIndex >= 0 && selectedIndex < titles.Count ? selectedIndex : -1;
        RebuildScopeMenu();
    }

    private void RebuildScopeMenu()
    {
        var actions = new UIMenuElement[_scopeTitles.Count];
        for (int index = 0; index < _scopeTitles.Count; index++)
        {
            int captured = index;
            var action = UIAction.Create(_scopeTitles[index], null, null, _ =>
            {
                if (_updating) return;
                _scopeIndex = captured;
                RebuildScopeMenu();
                ScopeChanged?.Invoke(this, captured);
            });
            action.State = index == _scopeIndex ? UIMenuElementState.On : UIMenuElementState.Off;
            actions[index] = action;
        }
        _scope.Menu = UIMenu.Create(actions);
        if (_scope.Configuration is { } configuration)
        {
            configuration.Title = _scopeIndex >= 0 ? _scopeTitles[_scopeIndex] : string.Empty;
            _scope.Configuration = configuration;
        }
        _scope.Hidden = _scopeTitles.Count == 0;
    }

    public string Status
    {
        get => _status.Text ?? string.Empty;
        set => _status.Text = value ?? string.Empty;
    }

    public void SetChips(IEnumerable<WinoSearchChip> chips)
    {
        var views = new List<UIView>();
        foreach (var spec in chips)
        {
            var chip = new WinoChipView(28)
            {
                Text = spec.Text,
                Prefix = spec.Prefix,
                Tag = spec,
                IsClickable = true,
                Fill = spec.IsActive ? WinoStyle.Accent.ColorWithAlpha(0.14f) : UIColor.SecondarySystemBackground,
                Stroke = spec.IsActive ? null : UIColor.Separator,
                TextColor = spec.IsActive ? WinoStyle.PrimaryText : WinoStyle.SecondaryText
            };
            if (spec.IsRemovable) chip.SetGlyph(WinoIconGlyph.Dismiss, 8);
            chip.AccessibilityTraits = UIAccessibilityTrait.Button | (spec.IsActive ? UIAccessibilityTrait.Selected : UIAccessibilityTrait.None);
            chip.Clicked += (_, _) => ChipClicked?.Invoke(this, spec);
            views.Add(chip);
        }
        if (!string.IsNullOrEmpty(FiltersTitle))
        {
            var title = WinoControlText.WithCount(FiltersTitle, ActiveFilterCount);
            var filters = new WinoChipView(28)
            {
                Text = title,
                IsClickable = true,
                Fill = ActiveFilterCount > 0 ? WinoStyle.Accent.ColorWithAlpha(0.14f) : UIColor.SecondarySystemBackground,
                Stroke = ActiveFilterCount > 0 ? null : UIColor.Separator,
                TextColor = WinoStyle.PrimaryText
            };
            filters.SetGlyph(WinoIconGlyph.Filter, 10);
            filters.AccessibilityLabel = title;
            filters.Clicked += (sender, _) => FiltersClicked?.Invoke(sender, EventArgs.Empty);
            views.Add(filters);
        }
        var previous = _chips.Subviews;
        _chips.SetItems(views);
        foreach (var old in previous) old.Dispose();
        _chips.Hidden = views.Count == 0;
    }

    private void AccentChanged(object? sender, EventArgs args)
    {
        if (_done.Configuration is not { } configuration) return;
        configuration.BaseForegroundColor = WinoStyle.Accent;
        _done.Configuration = configuration;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            WinoStyle.AccentWatchers.Remove(this);
            ReachChanged = null;
            ScopeChanged = null;
            DoneClicked = null;
            ChipClicked = null;
            FiltersClicked = null;
        }
        base.Dispose(disposing);
    }
}
