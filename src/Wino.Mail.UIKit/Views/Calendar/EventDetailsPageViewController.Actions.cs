using System.Collections.Specialized;
using System.Windows.Input;
using UIKit;
using Wino.Calendar.ViewModels;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Calendar;

/// <summary>
/// The action card (Mac action bar: Join online, RSVP, Delete, Edit series; Show as pop-up and Reminder pull-down as
/// UIMenu buttons), the RSVP panel and the commit-on-change saving of Show as and reminders. Each row scrolls
/// horizontally when it does not fit, so nothing clips at 320pt.
/// </summary>
public sealed partial class EventDetailsPageViewController
{
    private const double ActionRowHeight = 44;

    private readonly UIButton _join = ActionButton(WinoIconGlyph.EventJoinOnline, Translator.CalendarEventDetails_JoinOnline);
    private readonly UIButton _rsvp = ActionButton(WinoIconGlyph.EventRespond, Translator.CalendarEventResponse_NotResponded);
    private readonly UIButton _delete = ActionButton(WinoIconGlyph.Delete, Translator.Buttons_Delete);
    private readonly UIButton _series = ActionButton(WinoIconGlyph.EventEditSeries, Translator.CalendarEventDetails_EditSeries);
    private readonly UIButton _showAs = MenuButton(WinoIconGlyph.CalendarShowAs, Translator.CalendarEventDetails_ShowAs);
    private readonly UIButton _reminder = MenuButton(WinoIconGlyph.Reminder, Translator.CalendarEventDetails_Reminder);
    private readonly WinoSurfaceView _rsvpPanel = new() { Fill = CardFill, Stroke = WinoStyle.ZoneStroke, CornerRadius = WinoStyle.ControlRadius, Hidden = true };
    private readonly UITextField _rsvpMessage = new()
    {
        Placeholder = Translator.CalendarEventRsvpPanel_AddMessage,
        BorderStyle = UITextBorderStyle.RoundedRect,
        Font = WinoStyle.Subheadline,
        ReturnKeyType = UIReturnKeyType.Done,
        TranslatesAutoresizingMaskIntoConstraints = false
    };

    private bool _saving;
    private bool _savePending;
    private CalendarItemShowAs? _persistedShowAs;
    private bool _reminderMenuQueued;

    private UIView BuildActionCard()
    {
        BindCommand(_join, ViewModel.JoinOnlineCommand);
        BindCommand(_rsvp, ViewModel.ToggleRsvpPanelCommand);
        BindCommand(_delete, ViewModel.DeleteCommand);
        BindCommand(_series, ViewModel.ViewSeriesCommand);
        _series.Hidden = true;

        // The page is an inspector: a Show as choice or a reminder toggle commits at once (Windows context menu, Mac popups).
        UpdateShowAsMenu();
        _reminder.Menu = UIMenu.Create([UIDeferredMenuElement.CreateUncached(completion => completion(ReminderElements()))]);
        WinoAccessibility.Label(_showAs, Translator.CalendarEventDetails_ShowAs);
        WinoAccessibility.Label(_reminder, Translator.CalendarEventDetails_Reminder);
        UpdateReminderRow();

        var actions = HorizontalRow(2, _join, _rsvp, _delete, _series);
        var secondary = HorizontalRow(8, _showAs, _reminder);
        var column = WinoLayout.Insets(WinoLayout.VStack(2, actions, secondary), 4, 6, 4, 6);
        column.Alignment = UIStackViewAlignment.Fill;
        var card = new WinoSurfaceView { Fill = CardFill, CornerRadius = WinoStyle.ControlRadius };
        WinoLayout.Fill(column, card);
        return card;
    }

    private UIView BuildRsvpPanel()
    {
        var accept = RsvpButton(WinoIconGlyph.EventAccept, WinoStyle.Success, Translator.CalendarEventRsvpPanel_Accept, AttendeeStatus.Accepted);
        var tentative = RsvpButton(WinoIconGlyph.EventTentative, WinoStyle.Caution, Translator.CalendarEventRsvpPanel_Tentative, AttendeeStatus.Tentative);
        var decline = RsvpButton(WinoIconGlyph.EventDecline, WinoStyle.Critical, Translator.CalendarEventRsvpPanel_Decline, AttendeeStatus.Declined);

        var closeConfiguration = UIButtonConfiguration.PlainButtonConfiguration;
        closeConfiguration.Image = WinoIcons.Image(WinoIconGlyph.Dismiss, 14, null, Translator.Buttons_Close);
        closeConfiguration.BaseForegroundColor = WinoStyle.SecondaryText;
        var close = new UIButton(UIButtonType.System) { Configuration = closeConfiguration, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoAccessibility.Label(close, Translator.Buttons_Close);
        WinoLayout.Size(close, WinoStyle.MinimumHitTarget, WinoStyle.MinimumHitTarget);
        WinoLayout.Priorities(close, UILayoutConstraintAxis.Horizontal, 1000, 1000);
        BindCommand(close, ViewModel.CloseRsvpPanelCommand);

        var choices = HorizontalRow(2, accept, tentative, decline);
        WinoLayout.Priorities(choices, UILayoutConstraintAxis.Horizontal, 1, 1);
        var buttons = WinoLayout.HStack(4, choices, close);

        _rsvpMessage.EditingChanged += (_, _) => ViewModel.RsvpMessage = _rsvpMessage.Text ?? string.Empty;
        _rsvpMessage.ShouldReturn = field => { field.ResignFirstResponder(); return false; };
        WinoAccessibility.Label(_rsvpMessage, Translator.CalendarEventRsvpPanel_AddMessage);
        _rsvpMessage.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)36).Active = true;

        var column = WinoLayout.Insets(WinoLayout.VStack(8, buttons, _rsvpMessage), 8, 10, 10, 6);
        column.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(column, _rsvpPanel);
        return _rsvpPanel;
    }

    /// <summary>Windows TransparentActionButtonStyle on iOS: a plain button with a Wino glyph and a label.</summary>
    private static UIButton ActionButton(WinoIconGlyph glyph, string title, UIColor? tint = null)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Image = WinoIcons.Image(glyph, 16, tint, title);
        configuration.Title = title;
        configuration.ImagePadding = 6;
        configuration.BaseForegroundColor = WinoStyle.PrimaryText;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(6, 10, 6, 10);
        configuration.TitleTextAttributesTransformer = attributes =>
        {
            attributes[UIStringAttributeKey.Font] = WinoStyle.Fixed(15);
            return attributes;
        };
        var button = new UIButton(UIButtonType.System) { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        button.HeightAnchor.ConstraintEqualTo((nfloat)40).Active = true;
        WinoLayout.Priorities(button, UILayoutConstraintAxis.Horizontal, 1000, 1000);
        button.PointerInteractionEnabled = true;
        WinoAccessibility.Label(button, title);
        return button;
    }

    /// <summary>The Mac NSPopUpButton on iOS: a gray button with a glyph and the pop-up indicator that opens its UIMenu.</summary>
    private static UIButton MenuButton(WinoIconGlyph glyph, string title)
    {
        var configuration = UIButtonConfiguration.GrayButtonConfiguration;
        configuration.Image = WinoIcons.Image(glyph, 16, null, title);
        configuration.Title = title;
        configuration.ImagePadding = 6;
        configuration.Indicator = UIButtonConfigurationIndicator.Popup;
        configuration.CornerStyle = UIButtonConfigurationCornerStyle.Medium;
        configuration.TitleLineBreakMode = UILineBreakMode.TailTruncation;
        configuration.TitleTextAttributesTransformer = attributes =>
        {
            attributes[UIStringAttributeKey.Font] = WinoStyle.Fixed(15);
            return attributes;
        };
        var button = new UIButton(UIButtonType.System) { Configuration = configuration, ShowsMenuAsPrimaryAction = true, TranslatesAutoresizingMaskIntoConstraints = false };
        button.HeightAnchor.ConstraintEqualTo((nfloat)36).Active = true;
        WinoLayout.Priorities(button, UILayoutConstraintAxis.Horizontal, 1000, 1000);
        button.PointerInteractionEnabled = true;
        return button;
    }

    private UIButton RsvpButton(WinoIconGlyph glyph, UIColor tint, string title, AttendeeStatus status)
    {
        var button = ActionButton(glyph, title, tint);
        BindCommand(button, ViewModel.SendRsvpResponseCommand, () => status);
        return button;
    }

    /// <summary>Runs <paramref name="command"/> on tap and follows its CanExecute.</summary>
    private void BindCommand(UIButton button, ICommand command, Func<object?>? parameter = null)
    {
        var binding = Bindings.Own(new CommandBinding(command, parameter ?? (() => null), enabled => button.Enabled = enabled, Dispatcher, ReportError));
        EventHandler handler = (_, _) => binding.Execute();
        button.PrimaryActionTriggered += handler;
        Bindings.Own(new ActionDisposable(() => button.PrimaryActionTriggered -= handler));
    }

    /// <summary>A row that scrolls horizontally when its buttons are wider than the page.</summary>
    private static UIScrollView HorizontalRow(double spacing, params UIView[] views)
    {
        var stack = WinoLayout.HStack(spacing, views);
        var scroll = new UIScrollView
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            ShowsHorizontalScrollIndicator = false,
            ShowsVerticalScrollIndicator = false,
            AlwaysBounceVertical = false
        };
        scroll.AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints(
        [
            stack.LeadingAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.LeadingAnchor),
            stack.TrailingAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.TrailingAnchor),
            stack.TopAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.TopAnchor),
            stack.BottomAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.BottomAnchor),
            stack.HeightAnchor.ConstraintEqualTo(scroll.FrameLayoutGuide.HeightAnchor),
            scroll.HeightAnchor.ConstraintEqualTo((nfloat)ActionRowHeight)
        ]);
        return scroll;
    }

    private static void SetButtonTitle(UIButton button, string? title)
    {
        if (button.Configuration is not { } configuration || configuration.Title == title) return;
        configuration.Title = title;
        button.Configuration = configuration;
    }

    private static void SetButtonGlyph(UIButton button, WinoIconGlyph glyph, UIColor? tint, string? accessibilityLabel)
    {
        if (button.Configuration is not { } configuration) return;
        configuration.Image = WinoIcons.Image(glyph, 16, tint, accessibilityLabel);
        button.Configuration = configuration;
    }

    private void ApplyRsvp()
    {
        var (glyph, tint) = CalendarTileMapper.RsvpGlyph(ViewModel.CurrentRsvpStatus);
        var text = ViewModel.CurrentRsvpText ?? string.Empty;
        SetButtonGlyph(_rsvp, glyph, tint, text);
        SetButtonTitle(_rsvp, text);
        WinoAccessibility.Label(_rsvp, text);
    }

    // ------------------------------------------------------------ Show as and reminders

    /// <summary>Rebuilds the Show as menu with a check on the selected value and puts that value on the button.</summary>
    private void UpdateShowAsMenu()
    {
        var selected = ViewModel.SelectedShowAsOption;
        var actions = new List<UIMenuElement>();
        foreach (var option in ViewModel.ShowAsOptions)
        {
            var captured = option;
            var action = UIAction.Create(option.DisplayText, null, null, _ => ShowAsChosen(captured));
            action.State = ReferenceEquals(option, selected) ? UIMenuElementState.On : UIMenuElementState.Off;
            actions.Add(action);
        }
        _showAs.Menu = UIMenu.Create(Translator.CalendarEventDetails_ShowAs, null, UIMenuIdentifier.None, (UIMenuOptions)0, actions.ToArray());
        SetButtonTitle(_showAs, selected?.DisplayText ?? Translator.CalendarEventDetails_ShowAs);
    }

    private void ShowAsChosen(ShowAsOption option)
    {
        if (ReferenceEquals(option, ViewModel.SelectedShowAsOption)) return;
        ViewModel.SelectedShowAsOption = option;
        UpdateShowAsMenu();
        RequestSave();
    }

    /// <summary>The Reminder pull-down, built each time it opens: every option toggles its check and commits.</summary>
    private UIMenuElement[] ReminderElements()
    {
        var elements = new List<UIMenuElement>();
        foreach (var option in ViewModel.ReminderOptions)
        {
            var captured = option;
            var action = UIAction.Create(option.DisplayText, null, null, _ => ToggleReminder(captured));
            action.State = option.IsSelected ? UIMenuElementState.On : UIMenuElementState.Off;
            if (_isReadOnly) action.Attributes = UIMenuElementAttributes.Disabled;
            elements.Add(action);
        }
        return elements.ToArray();
    }

    private void ToggleReminder(ReminderOption option)
    {
        option.IsSelected = !option.IsSelected;
        UpdateReminderRow();
        RequestSave();
    }

    private void ReminderOptionsChanged(object? sender, NotifyCollectionChangedEventArgs args) => QueueReminderMenu();

    /// <summary>The ViewModel replaces the reminder options when an event loads; refresh once per burst.</summary>
    private void QueueReminderMenu()
    {
        if (_reminderMenuQueued) return;
        _reminderMenuQueued = true;
        _ = Dispatcher.ExecuteOnUIThread(() =>
        {
            _reminderMenuQueued = false;
            if (!_released) UpdateReminderRow();
        });
    }

    private void UpdateReminderRow()
    {
        var selected = ViewModel.ReminderOptions.Where(option => option.IsSelected).Select(option => option.DisplayText).ToList();
        _reminderRow.Text = selected.Count == 0 ? null : string.Join(", ", selected);
    }

    // ------------------------------------------------------------ commit on change

    /// <summary>
    /// Saves the Show as and reminder choice. Saves never overlap: a request during a save runs one more save afterwards
    /// (the shared command refuses concurrent executions).
    /// </summary>
    private void RequestSave()
    {
        if (_isReadOnly || _released || ViewModel.CurrentEvent is null) return;
        if (_saving) { _savePending = true; return; }
        _ = SaveLoopAsync();
    }

    private async Task SaveLoopAsync()
    {
        _saving = true;
        try
        {
            do
            {
                _savePending = false;
                var persisted = _persistedShowAs;
                await ViewModel.SaveCommand.ExecuteAsync(null);
                bool succeeded = ViewModel.LastMutationSucceeded;
                await Dispatcher.ExecuteOnUIThread(() =>
                {
                    if (_released) return;
                    if (succeeded) _persistedShowAs = ViewModel.SelectedShowAsOption?.ShowAs ?? persisted;
                    else { RevertToPersisted(persisted); _savePending = false; }
                });
            }
            while (_savePending && !_released);
        }
        catch (Exception exception) { ReportError(exception); }
        finally { _saving = false; }
    }

    /// <summary>A failed save leaves the controls on the persisted values, not on the rejected choice.</summary>
    private void RevertToPersisted(CalendarItemShowAs? persistedShowAs)
    {
        if (persistedShowAs is { } showAs)
        {
            if (ViewModel.CurrentEvent?.CalendarItem is { } calendarItem) calendarItem.ShowAs = showAs;
            ViewModel.SelectedShowAsOption = ViewModel.ShowAsOptions.FirstOrDefault(option => option.ShowAs == showAs) ?? ViewModel.SelectedShowAsOption;
            UpdateShowAsMenu();
        }
        var minutes = (ViewModel.Reminders ?? []).Select(reminder => (int)(reminder.DurationInSeconds / 60)).ToHashSet();
        foreach (var option in ViewModel.ReminderOptions) option.IsSelected = minutes.Contains(option.Minutes);
        UpdateReminderRow();
    }
}
