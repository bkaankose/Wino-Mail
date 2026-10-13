using CoreGraphics;
using UIKit;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Calendar preferences: overlapping event layout with its live preview, sync interval, account grouping with the
/// startup account, and the New event behaviour (Windows CalendarPreferenceSettingsPage).
/// </summary>
public sealed class CalendarPreferenceSettingsPageViewController(CalendarPreferenceSettingsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<CalendarPreferenceSettingsPageViewModel>(viewModel, dispatcher, logger)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;

        var modeDescription = WinoStyle.Label(string.Empty, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        var modePopup = Bind.PopUp<CalendarPreferenceSettingsPageViewModel, CalendarEventDisplayModeOption>(vm, s => s.EventDisplayModeOptions, option => option.DisplayName,
            nameof(vm.SelectedEventDisplayModeOption), s => s.SelectedEventDisplayModeOption, (s, v) => s.SelectedEventDisplayModeOption = v);
        Bind.Bind(vm, nameof(vm.SelectedEventDisplayModeOption), s => s.SelectedEventDisplayModeOption?.Description, text => modeDescription.Text = text ?? string.Empty);
        var overlapCard = Card(Translator.CalendarSettings_OverlappingEvents_Header, null);
        overlapCard.BottomContent = WinoLayout.VStack(WinoStyle.Space2, modePopup, modeDescription);
        var overlap = new WinoSettingsExpander(Translator.CalendarSettings_Overlap_Header, Translator.CalendarSettings_Overlap_Description, isExpanded: true);
        overlap.Add(overlapCard);

        var preview = new OverlapPreviewView();
        Bind.Bind(vm, nameof(vm.OverlapPreviewItems), s => s.OverlapPreviewItems, items => preview.Items = items);
        var previewCard = Card(Translator.CalendarSettings_Overlap_Preview, Translator.CalendarSettings_Overlap_PreviewDescription);
        previewCard.BottomContent = SettingsCardLayout.Leading(preview);

        var sync = StackedCard(Translator.CalendarSettings_SyncInterval_Title, Translator.CalendarSettings_SyncInterval_Description, WinoIconGlyph.Sync,
            SettingsCardLayout.Leading(Bind.Stepper(vm, nameof(vm.CalendarSyncIntervalMinutes), s => s.CalendarSyncIntervalMinutes, (s, v) => s.CalendarSyncIntervalMinutes = v, 1, 1440,
                accessibilityLabel: Translator.CalendarSettings_SyncInterval_Title)));

        var startupHint = WinoStyle.Label(Translator.CalendarSettings_StartupAccount_Description, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        var startupPopup = Bind.PopUp<CalendarPreferenceSettingsPageViewModel, MailAccount>(vm, s => s.CalendarStartupAccounts.ToList(),
            account => string.IsNullOrWhiteSpace(account.Name) ? account.Address ?? string.Empty : $"{account.Name} — {account.Address}",
            nameof(vm.SelectedCalendarStartupAccount), s => s.SelectedCalendarStartupAccount, (s, v) => s.SelectedCalendarStartupAccount = v,
            itemsCollection: vm.CalendarStartupAccounts);
        var startup = WinoLayout.VStack(6, Caption(Translator.CalendarSettings_StartupAccount_Header), startupHint, startupPopup);
        startup.Alignment = UIStackViewAlignment.Fill;
        Bind.Visible(startup, vm, nameof(vm.ShouldShowCalendarStartupAccount), s => s.ShouldShowCalendarStartupAccount);
        var grouping = Card(Translator.CalendarSettings_AccountGrouping_Header, Translator.CalendarSettings_AccountGrouping_Description, WinoIconGlyph.Folder,
            Bind.Switch(vm, nameof(vm.IsCalendarAccountsGrouped), s => s.IsCalendarAccountsGrouped, (s, v) => s.IsCalendarAccountsGrouped = v, Translator.CalendarSettings_AccountGrouping_Header));
        grouping.BottomContent = startup;

        var behavior = Bind.PopUp<CalendarPreferenceSettingsPageViewModel, CalendarNewEventBehaviorOption>(vm, s => s.NewEventBehaviorOptions.ToList(), option => option.DisplayText,
            nameof(vm.SelectedNewEventBehaviorOption), s => s.SelectedNewEventBehaviorOption, (s, v) => s.SelectedNewEventBehaviorOption = v, itemsCollection: vm.NewEventBehaviorOptions);
        var calendar = Bind.PopUp<CalendarPreferenceSettingsPageViewModel, AccountCalendarViewModel>(vm, s => s.AvailableNewEventCalendars.ToList(),
            item => $"{item.Name} — {item.Account?.Address}",
            nameof(vm.SelectedNewEventCalendar), s => s.SelectedNewEventCalendar, (s, v) => s.SelectedNewEventCalendar = v, itemsCollection: vm.AvailableNewEventCalendars);
        Bind.Visible(calendar, vm, nameof(vm.ShouldShowSpecificNewEventCalendar), s => s.ShouldShowSpecificNewEventCalendar);
        var newEventControls = WinoLayout.VStack(WinoStyle.Space2, behavior, calendar);
        newEventControls.Alignment = UIStackViewAlignment.Fill;
        var newEvent = StackedCard(Translator.CalendarSettings_NewEventBehavior_Header, Translator.CalendarSettings_NewEventBehavior_Description, WinoIconGlyph.Calendar, newEventControls);

        AddGroup(null, overlap, previewCard, sync, grouping, newEvent);
    }

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        await base.InitializeAsync(mode, parameter);
        try { await ViewModel.InitializationTask; }
        catch (Exception exception) { ReportError(exception); }
    }

    /// <summary>The Windows 410x196 overlap sample, scaled to the card width: an hour gutter, hour lines and the placed sample events.</summary>
    private sealed class OverlapPreviewView : UIView
    {
        private const double SampleWidth = 410, SampleHeight = 196, Gutter = 44;
        private IReadOnlyList<CalendarOverlapPreviewItem> _items = Array.Empty<CalendarOverlapPreviewItem>();

        public OverlapPreviewView()
        {
            TranslatesAutoresizingMaskIntoConstraints = false;
            BackgroundColor = UIColor.Clear;
            ContentMode = UIViewContentMode.Redraw;
            IsAccessibilityElement = false;
            var preferred = WidthAnchor.ConstraintEqualTo((nfloat)SampleWidth);
            preferred.Priority = 700;
            NSLayoutConstraint.ActivateConstraints(
            [
                WidthAnchor.ConstraintLessThanOrEqualTo((nfloat)SampleWidth),
                preferred,
                HeightAnchor.ConstraintEqualTo(WidthAnchor, (nfloat)(SampleHeight / SampleWidth))
            ]);
            WinoTraits.OnAppearanceChanged(this, static view => view.SetNeedsDisplay());
        }

        public IReadOnlyList<CalendarOverlapPreviewItem>? Items
        {
            set { _items = value ?? Array.Empty<CalendarOverlapPreviewItem>(); SetNeedsDisplay(); }
        }

        public override void Draw(CGRect rect)
        {
            if (UIGraphics.GetCurrentContext() is not { } context) return;
            var scale = Bounds.Width / SampleWidth;
            context.SaveState();
            context.ScaleCTM((nfloat)scale, (nfloat)scale);

            WinoSettingsStyle.SubtleFill.SetFill();
            UIGraphics.RectFill(new CGRect(0, 0, SampleWidth, SampleHeight));
            var caption = new UIStringAttributes { Font = WinoStyle.Fixed(11), ForegroundColor = WinoStyle.SecondaryText };
            foreach (var (label, top) in new[] { ("09:00", 4.0), ("10:00", 64.0), ("11:00", 124.0) })
                new Foundation.NSAttributedString(label, caption).DrawString(new CGPoint(0, top - 2));
            WinoSettingsStyle.CardStroke.SetFill();
            foreach (var top in new[] { 4.0, 64.0, 124.0, 184.0 })
                UIGraphics.RectFill(new CGRect(Gutter, top, 360, 1));

            var title = new UIStringAttributes { Font = WinoStyle.Fixed(11), ForegroundColor = UIColor.White };
            foreach (var item in _items)
            {
                var box = new CGRect(Gutter + item.Left, 4 + item.Top, item.Width, item.Height);
                var path = UIBezierPath.FromRoundedRect(box, 4);
                WinoStyle.Accent.SetFill();
                path.Fill();
                UIColor.White.ColorWithAlpha(0.7f).SetStroke();
                path.LineWidth = 1;
                path.Stroke();
                new Foundation.NSAttributedString(item.Title, title)
                    .DrawString(new CGRect(box.X + 6, box.Y + 4, Math.Max(0, box.Width - 12), 16));
            }
            context.RestoreState();
        }
    }
}
