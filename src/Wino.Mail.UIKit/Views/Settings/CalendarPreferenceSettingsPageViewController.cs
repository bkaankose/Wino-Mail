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
/// Calendar preferences (Windows CalendarPreferenceSettingsPage): the overlap behaviour as one value row with its live
/// preview under it on the page, the sync interval, account grouping with the startup account as the next row, and the
/// New event behaviour with the specific calendar as the next row. Every choice is one push deep (18 Oct 2026).
/// </summary>
public sealed class CalendarPreferenceSettingsPageViewController(CalendarPreferenceSettingsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<CalendarPreferenceSettingsPageViewModel>(viewModel, dispatcher, logger)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;

        // Overlap behaviour is one value row: its push is the option list itself (18 Oct 2026, no page in between), and
        // the live preview sits on this page under it.
        var modePopup = Bind.PopUp<CalendarPreferenceSettingsPageViewModel, CalendarEventDisplayModeOption>(vm, s => s.EventDisplayModeOptions, option => option.DisplayName,
            nameof(vm.SelectedEventDisplayModeOption), s => s.SelectedEventDisplayModeOption, (s, v) => s.SelectedEventDisplayModeOption = v);
        var overlap = Card(Translator.CalendarSettings_Overlap_Header, Translator.CalendarSettings_Overlap_Description, WinoIconGlyph.None, modePopup);
        AddGroup(null, overlap);

        var preview = new OverlapPreviewView();
        Bind.Bind(vm, nameof(vm.OverlapPreviewItems), s => s.OverlapPreviewItems, items => preview.Items = items);
        var modeDescription = WinoStyle.Label(string.Empty, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        Bind.Bind(vm, nameof(vm.SelectedEventDisplayModeOption), s => s.SelectedEventDisplayModeOption?.Description, text => modeDescription.Text = text ?? string.Empty);
        Add(PreviewSurface(preview, modeDescription));

        var sync = StackedCard(Translator.CalendarSettings_SyncInterval_Title, Translator.CalendarSettings_SyncInterval_Description, WinoIconGlyph.Sync,
            SettingsCardLayout.Leading(Bind.Stepper(vm, nameof(vm.CalendarSyncIntervalMinutes), s => s.CalendarSyncIntervalMinutes, (s, v) => s.CalendarSyncIntervalMinutes = v, 1, 1440,
                accessibilityLabel: Translator.CalendarSettings_SyncInterval_Title)));

        // Grouping is a switch row; the startup account follows as its own value row while accounts are not grouped.
        var grouping = Card(Translator.CalendarSettings_AccountGrouping_Header, Translator.CalendarSettings_AccountGrouping_Description, WinoIconGlyph.Folder,
            Bind.Switch(vm, nameof(vm.IsCalendarAccountsGrouped), s => s.IsCalendarAccountsGrouped, (s, v) => s.IsCalendarAccountsGrouped = v, Translator.CalendarSettings_AccountGrouping_Header));
        var startupPopup = Bind.PopUp<CalendarPreferenceSettingsPageViewModel, MailAccount>(vm, s => s.CalendarStartupAccounts.ToList(),
            account => string.IsNullOrWhiteSpace(account.Name) ? account.Address ?? string.Empty : $"{account.Name} — {account.Address}",
            nameof(vm.SelectedCalendarStartupAccount), s => s.SelectedCalendarStartupAccount, (s, v) => s.SelectedCalendarStartupAccount = v,
            itemsCollection: vm.CalendarStartupAccounts);
        var startup = Card(Translator.CalendarSettings_StartupAccount_Header, Translator.CalendarSettings_StartupAccount_Description, WinoIconGlyph.None, startupPopup);
        Bind.Visible(startup, vm, nameof(vm.ShouldShowCalendarStartupAccount), s => s.ShouldShowCalendarStartupAccount);

        // The New Event behaviour and, when it names one, the calendar it opens: two value rows.
        var behavior = Bind.PopUp<CalendarPreferenceSettingsPageViewModel, CalendarNewEventBehaviorOption>(vm, s => s.NewEventBehaviorOptions.ToList(), option => option.DisplayText,
            nameof(vm.SelectedNewEventBehaviorOption), s => s.SelectedNewEventBehaviorOption, (s, v) => s.SelectedNewEventBehaviorOption = v, itemsCollection: vm.NewEventBehaviorOptions);
        var newEvent = Card(Translator.CalendarSettings_NewEventBehavior_Header, Translator.CalendarSettings_NewEventBehavior_Description, WinoIconGlyph.Calendar, behavior);
        var calendarPopup = Bind.PopUp<CalendarPreferenceSettingsPageViewModel, AccountCalendarViewModel>(vm, s => s.AvailableNewEventCalendars.ToList(),
            item => $"{item.Name} — {item.Account?.Address}",
            nameof(vm.SelectedNewEventCalendar), s => s.SelectedNewEventCalendar, (s, v) => s.SelectedNewEventCalendar = v, itemsCollection: vm.AvailableNewEventCalendars);
        var calendar = Card(Translator.CalendarSettings_NewEventCalendar_Header, Translator.CalendarSettings_NewEventCalendar_Description, WinoIconGlyph.None, calendarPopup);
        Bind.Visible(calendar, vm, nameof(vm.ShouldShowSpecificNewEventCalendar), s => s.ShouldShowSpecificNewEventCalendar);

        AddGroup(null, vm.IsSyncIntervalAvailable ? sync : null, grouping, startup, newEvent, calendar);
    }

    /// <summary>The preview sample on its own surface, with the selected mode's explanation under it.</summary>
    private static UIView PreviewSurface(UIView preview, UILabel description)
    {
        var caption = WinoStyle.Label(Translator.CalendarSettings_Overlap_Preview, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText);
        var content = WinoLayout.VStack(WinoStyle.Space2, caption, SettingsCardLayout.Leading(preview), description);
        content.Alignment = UIStackViewAlignment.Fill;
        var surface = new WinoSurfaceView
        {
            Fill = WinoSettingsStyle.GroupFill,
            Stroke = WinoSettingsStyle.CardStroke,
            StrokeWidth = 1,
            CornerRadius = WinoSettingsStyle.CardRadius,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Fill(content, surface, 12, WinoSettingsStyle.CardPadding, 12, WinoSettingsStyle.CardPadding);
        return surface;
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
