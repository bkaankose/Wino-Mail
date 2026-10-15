using Android.Content;
using Android.Graphics;
using Android.Views;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Android.Settings;
using Wino.Presentation.Android;
using View = Android.Views.View;

namespace Wino.Mail.Android.Views.Settings.CalendarContactsToDo;

/// <summary>
/// Calendar preferences: overlapping event layout with its live preview, sync interval, account grouping with the
/// startup account, and the New event behaviour (Windows CalendarPreferenceSettingsPage, iOS
/// CalendarPreferenceSettingsPageViewController).
/// </summary>
public sealed class CalendarPreferenceSettingsPageFragment : SettingsPageFragment<CalendarPreferenceSettingsPageViewModel>
{
    /// <summary>The iOS InitializeAsync: OnNavigatedTo, then the calendar option load.</summary>
    public static async Task InitializeAsync(CalendarPreferenceSettingsPageViewModel viewModel, NavigationMode mode, object? parameter)
    {
        viewModel.OnNavigatedTo(mode, parameter);
        try { await viewModel.InitializationTask; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Calendar preferences failed to load."); }
    }

    protected override void BuildPage()
    {
        var vm = VM;

        var modeDescription = Text(string.Empty, WinoTextStyle.Description);
        var modePopup = Bind.PopUp<CalendarPreferenceSettingsPageViewModel, CalendarEventDisplayModeOption>(vm, s => s.EventDisplayModeOptions, option => option.DisplayName,
            nameof(vm.SelectedEventDisplayModeOption), s => s.SelectedEventDisplayModeOption, (s, v) => s.SelectedEventDisplayModeOption = v);
        Bind.Bind(vm, nameof(vm.SelectedEventDisplayModeOption), s => s.SelectedEventDisplayModeOption?.Description, text => modeDescription.Text = text ?? string.Empty);
        var overlapCard = Card(Translator.CalendarSettings_OverlappingEvents_Header, null);
        overlapCard.BottomContent = Stack(Resource.Dimension.wino_space_s, modePopup, modeDescription);
        var overlap = new WinoSettingsExpander(Ctx, Translator.CalendarSettings_Overlap_Header, Translator.CalendarSettings_Overlap_Description, isExpanded: true);
        overlap.Add(overlapCard);

        var preview = new OverlapPreviewView(Ctx);
        Bind.Bind(vm, nameof(vm.OverlapPreviewItems), s => s.OverlapPreviewItems, items => preview.Items = items);
        var previewCard = Card(Translator.CalendarSettings_Overlap_Preview, Translator.CalendarSettings_Overlap_PreviewDescription);
        previewCard.BottomContent = preview;

        var sync = StackedCard(Translator.CalendarSettings_SyncInterval_Title, Translator.CalendarSettings_SyncInterval_Description, WinoIconGlyph.Sync,
            Row(Bind.Stepper(vm, nameof(vm.CalendarSyncIntervalMinutes), s => s.CalendarSyncIntervalMinutes, (s, v) => s.CalendarSyncIntervalMinutes = v, 1, 1440,
                accessibilityLabel: Translator.CalendarSettings_SyncInterval_Title)));

        var startupPopup = Bind.PopUp<CalendarPreferenceSettingsPageViewModel, MailAccount>(vm, s => s.CalendarStartupAccounts.ToList(),
            account => string.IsNullOrWhiteSpace(account.Name) ? account.Address ?? string.Empty : $"{account.Name} — {account.Address}",
            nameof(vm.SelectedCalendarStartupAccount), s => s.SelectedCalendarStartupAccount, (s, v) => s.SelectedCalendarStartupAccount = v,
            itemsCollection: vm.CalendarStartupAccounts);
        var startup = Stack(Resource.Dimension.wino_space_xs, Caption(Translator.CalendarSettings_StartupAccount_Header), Text(Translator.CalendarSettings_StartupAccount_Description, WinoTextStyle.Description), startupPopup);
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
        var newEvent = StackedCard(Translator.CalendarSettings_NewEventBehavior_Header, Translator.CalendarSettings_NewEventBehavior_Description, WinoIconGlyph.Calendar,
            Stack(Resource.Dimension.wino_space_s, behavior, calendar));

        AddGroup(null, overlap, previewCard, sync, grouping, newEvent);
    }

    /// <summary>
    /// The Windows 410x196 overlap sample, scaled to the card width: an hour gutter, hour lines and the placed sample
    /// events. The sample geometry (gutter, hour rows, event boxes) is in the ViewModel's sample units; text, radii,
    /// strokes and insets come from tokens, read in dp because one sample unit is one dp at the preferred width.
    /// </summary>
    private sealed class OverlapPreviewView : View
    {
        private const float SampleWidth = 410, SampleHeight = 196, Gutter = 44, HourLineWidth = 360, SampleTop = 4;
        private static readonly (string Label, float Top)[] HourLabels = [("09:00", 4f), ("10:00", 64f), ("11:00", 124f)];
        private static readonly float[] HourLines = [4f, 64f, 124f, 184f];
        private readonly Paint _paint = new(PaintFlags.AntiAlias);
        private IReadOnlyList<CalendarOverlapPreviewItem> _items = Array.Empty<CalendarOverlapPreviewItem>();

        public OverlapPreviewView(Context context) : base(context)
        {
            ImportantForAccessibility = ImportantForAccessibility.No;
        }

        public IReadOnlyList<CalendarOverlapPreviewItem>? Items
        {
            set
            {
                _items = value ?? Array.Empty<CalendarOverlapPreviewItem>();
                Invalidate();
            }
        }

        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
        {
            int available = MeasureSpec.GetSize(widthMeasureSpec);
            int preferred = Context is { } context ? WinoTokens.Px(context, Resource.Dimension.wino_calendar_overlap_preview_width) : available;
            int width = MeasureSpec.GetMode(widthMeasureSpec) == MeasureSpecMode.Unspecified ? preferred : Math.Min(available, preferred);
            SetMeasuredDimension(width, (int)Math.Round(width * SampleHeight / SampleWidth));
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            if (Context is not { } context || Width <= 0) return;
            float Unit(int dimen) => (float)WinoTokens.Dp(context, dimen);
            float textSize = Unit(Resource.Dimension.wino_text_tiny);
            float radius = Unit(Resource.Dimension.wino_radius_small);
            float stroke = Unit(Resource.Dimension.wino_space_hairline);
            float insetX = Unit(Resource.Dimension.wino_space_xs);
            float insetY = Unit(Resource.Dimension.wino_space_2xs);

            float scale = Width / SampleWidth;
            canvas.Save();
            canvas.Scale(scale, scale);

            _paint.SetStyle(Paint.Style.Fill);
            _paint.Color = WinoColors.SubtleFill(context);
            canvas.DrawRect(0, 0, SampleWidth, SampleHeight, _paint);

            // Sizes are in sample units; the canvas scales them to the card width.
            _paint.TextSize = textSize;
            _paint.Color = WinoColors.SecondaryText(context);
            foreach (var (label, top) in HourLabels)
                canvas.DrawText(label, 0, top + textSize - Unit(Resource.Dimension.wino_space_3xs), _paint);
            _paint.Color = WinoColors.CardStroke(context);
            foreach (var top in HourLines)
                canvas.DrawRect(Gutter, top, Gutter + HourLineWidth, top + stroke, _paint);

            var eventText = WinoTokens.Color(context, Resource.Color.wino_calendar_preview_event_text);
            var eventOutline = WinoTokens.Color(context, Resource.Color.wino_calendar_preview_event_outline);
            foreach (var item in _items)
            {
                var box = new RectF(Gutter + (float)item.Left, SampleTop + (float)item.Top, Gutter + (float)(item.Left + item.Width), SampleTop + (float)(item.Top + item.Height));
                _paint.SetStyle(Paint.Style.Fill);
                _paint.Color = WinoColors.Accent(context);
                canvas.DrawRoundRect(box, radius, radius, _paint);
                _paint.SetStyle(Paint.Style.Stroke);
                _paint.StrokeWidth = stroke;
                _paint.Color = eventOutline;
                canvas.DrawRoundRect(box, radius, radius, _paint);

                _paint.SetStyle(Paint.Style.Fill);
                _paint.Color = eventText;
                float baseline = box.Top + insetY + textSize;
                canvas.Save();
                canvas.ClipRect(box.Left + insetX, box.Top + insetY, Math.Max(box.Left + insetX, box.Right - insetX), baseline + insetY + stroke);
                canvas.DrawText(item.Title ?? string.Empty, box.Left + insetX, baseline, _paint);
                canvas.Restore();
            }
            canvas.Restore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _paint.Dispose();
            base.Dispose(disposing);
        }
    }
}
