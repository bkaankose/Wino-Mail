using System.Collections.Specialized;
using System.ComponentModel;
using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Calendar.ViewModels.Data;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Core.Domain.Enums;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Calendar;

/// <summary>
/// The calendars list (Windows AccountCalendarGroupTemplate; Mac ShellCalendarGroupCell): per account a header with a
/// three-state check, the account colour, name and address (or the sync status) and an expand chevron, then a row per
/// calendar with its colour check while expanded. Bound to <see cref="IAccountCalendarStateService"/>: toggling a check
/// sets <see cref="AccountCalendarViewModel.IsChecked"/> or <see cref="GroupedAccountCalendarViewModel.IsCheckedState"/>,
/// which the calendar page reacts to. Used in the iPhone calendars sheet and the iPad calendar sidebar.
/// </summary>
public sealed class CalendarListView : UIView
{
    public const double HeaderHeight = 52;
    public const double CalendarRowHeight = 44;

    private readonly IAccountCalendarStateService _state;
    private readonly UIStackView _stack = WinoLayout.VStack(0);
    private readonly List<INotifyPropertyChanged> _observed = [];
    private readonly List<INotifyCollectionChanged> _collections = [];
    private bool _rebuildQueued;
    private bool _disposed;

    public CalendarListView(IAccountCalendarStateService state)
    {
        _state = state;
        TranslatesAutoresizingMaskIntoConstraints = false;
        _stack.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(_stack, this);
        if (state.GroupedAccountCalendars is INotifyCollectionChanged groups) Track(groups);
        Rebuild();
    }

    /// <summary>Text colour of the rows (primary text by default; the sidebar keeps it).</summary>
    public UIColor TextColor { get; set; } = WinoStyle.PrimaryText;

    /// <summary>Number of calendars on screen, for diagnostics.</summary>
    public int CalendarRowCount { get; private set; }

    private void Track(INotifyCollectionChanged collection)
    {
        collection.CollectionChanged += CollectionChanged;
        _collections.Add(collection);
    }

    private void Observe(INotifyPropertyChanged source)
    {
        source.PropertyChanged += PropertyChanged;
        _observed.Add(source);
    }

    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => QueueRebuild();

    private void PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(GroupedAccountCalendarViewModel.TotalItemsToSync) or nameof(GroupedAccountCalendarViewModel.RemainingItemsToSync)) return;
        QueueRebuild();
    }

    /// <summary>Coalesces bursts (sync ticks, a group toggling all its calendars) into one rebuild on the main loop.</summary>
    private void QueueRebuild()
    {
        if (_rebuildQueued || _disposed) return;
        _rebuildQueued = true;
        BeginInvokeOnMainThread(() =>
        {
            _rebuildQueued = false;
            if (!_disposed) Rebuild();
        });
    }

    private void Detach()
    {
        foreach (var source in _observed) source.PropertyChanged -= PropertyChanged;
        _observed.Clear();
        // The groups collection stays tracked; the per-group calendar collections are re-tracked on each rebuild.
        for (int index = _collections.Count - 1; index >= 1; index--)
        {
            _collections[index].CollectionChanged -= CollectionChanged;
            _collections.RemoveAt(index);
        }
    }

    public void Rebuild()
    {
        Detach();
        foreach (var view in _stack.ArrangedSubviews)
        {
            _stack.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
        }
        int count = 0;
        foreach (var group in _state.GroupedAccountCalendars)
        {
            Observe(group);
            Track(group.AccountCalendars);
            _stack.AddArrangedSubview(Header(group));
            if (!group.IsExpanded) continue;
            foreach (var calendar in group.AccountCalendars)
            {
                Observe(calendar);
                _stack.AddArrangedSubview(CalendarRow(calendar));
                count++;
            }
        }
        CalendarRowCount = count;
    }

    private UIView Header(GroupedAccountCalendarViewModel group)
    {
        var fill = WinoStyle.FromHexString(group.AccountColorHex) ?? WinoStyle.AvatarColor(group.Account?.Name);
        var glyph = group.IsCheckedState switch
        {
            true => WinoIconGlyph.CheckmarkCircleFilled,
            false => WinoIconGlyph.Circle,
            _ => WinoIconGlyph.SubtractCircle
        };
        var check = CheckButton(glyph, fill, group.Account?.Name, () => group.IsCheckedState = group.IsCheckedState != true);

        var name = WinoStyle.Label(group.Account?.Name ?? string.Empty, WinoStyle.Scaled(15, UIFontWeight.Semibold), TextColor);
        bool syncing = group.IsSynchronizationProgressVisible && !string.IsNullOrWhiteSpace(group.SynchronizationStatus);
        var detail = WinoStyle.Label(syncing ? group.SynchronizationStatus : group.Account?.Address ?? string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
        var text = WinoLayout.VStack(1, name, detail);
        text.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        name.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
        detail.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);

        var chevron = new UIButton { TranslatesAutoresizingMaskIntoConstraints = false };
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Image = WinoIcons.Image(group.IsExpanded ? WinoIconGlyph.ChevronDown : WinoIconGlyph.ChevronRight, 12, WinoStyle.SecondaryText);
        chevron.Configuration = configuration;
        WinoLayout.Size(chevron, 44, 44);
        WinoAccessibility.Label(chevron, group.Account?.Name);
        chevron.AccessibilityTraits = UIAccessibilityTrait.Button | (group.IsExpanded ? UIAccessibilityTrait.Selected : UIAccessibilityTrait.None);
        chevron.PrimaryActionTriggered += (_, _) => group.IsExpanded = !group.IsExpanded;

        var row = WinoLayout.Insets(WinoLayout.HStack(10, check, text, chevron), 0, 8, 0, 4);
        row.Alignment = UIStackViewAlignment.Center;
        row.HeightAnchor.ConstraintEqualTo((nfloat)HeaderHeight).Active = true;
        return row;
    }

    private UIView CalendarRow(AccountCalendarViewModel calendar)
    {
        var fill = WinoStyle.FromHexString(calendar.BackgroundColorHex) ?? WinoStyle.Accent;
        var check = CheckButton(calendar.IsChecked ? WinoIconGlyph.CheckmarkCircleFilled : WinoIconGlyph.Circle, fill, calendar.Name,
            () => calendar.IsChecked = !calendar.IsChecked);
        var name = WinoStyle.Label(calendar.Name ?? string.Empty, WinoStyle.Subheadline, TextColor);
        name.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
        var views = new List<UIView> { check, name, WinoLayout.Spacer() };
        if (calendar.IsReadOnly) views.Add(new WinoIconView(WinoIconGlyph.PersonLock, 14, WinoStyle.TertiaryText));
        var row = WinoLayout.Insets(WinoLayout.HStack(10, views.ToArray()), 0, 30, 0, 14);
        row.Alignment = UIStackViewAlignment.Center;
        row.HeightAnchor.ConstraintEqualTo((nfloat)CalendarRowHeight).Active = true;
        // The whole row toggles, like a checkbox label.
        row.AddGestureRecognizer(new UITapGestureRecognizer(() => calendar.IsChecked = !calendar.IsChecked));
        row.IsAccessibilityElement = true;
        row.AccessibilityLabel = calendar.Name;
        row.AccessibilityTraits = UIAccessibilityTrait.Button | (calendar.IsChecked ? UIAccessibilityTrait.Selected : UIAccessibilityTrait.None);
        return row;
    }

    private static UIButton CheckButton(WinoIconGlyph glyph, UIColor tint, string? label, Action toggle)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Image = WinoIcons.Image(glyph, 22, tint);
        configuration.ContentInsets = new NSDirectionalEdgeInsets(0, 0, 0, 0);
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(button, 36, 36);
        WinoAccessibility.Label(button, label);
        button.PrimaryActionTriggered += (_, _) => toggle();
        return button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            Detach();
            foreach (var collection in _collections) collection.CollectionChanged -= CollectionChanged;
            _collections.Clear();
        }
        base.Dispose(disposing);
    }
}
