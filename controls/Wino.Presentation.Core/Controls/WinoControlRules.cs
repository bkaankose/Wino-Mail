using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.Core.AccountIcon;
using Wino.Mail.Controls.Core.IntelligenceHeader;

namespace Wino.Presentation.Controls;

/// <summary>One segment of the mode switcher card: its mode, regular and selected (filled) glyph, title and shortcut.</summary>
public sealed record WinoModeSegment(WinoApplicationMode Mode, WinoIconGlyph Regular, WinoIconGlyph Filled, string Title, string Shortcut);

/// <summary>
/// The segments of the Windows AppModeFooterSwitcherControl in desktop order (Mail, Calendar, Contacts, To Do,
/// Settings), shared by the AppKit footer and the iPad sidebar card.
/// </summary>
public static class WinoModeSegments
{
    /// <summary>The four app modes and Settings, as on Windows and the Mac.</summary>
    public static IReadOnlyList<WinoModeSegment> All { get; } = (WinoModeSegment[])
    [
        new(WinoApplicationMode.Mail, WinoIconGlyph.Mail, WinoIconGlyph.MailFilled, Translator.KeyboardShortcuts_ModeMail, "⌘1"),
        new(WinoApplicationMode.Calendar, WinoIconGlyph.Calendar, WinoIconGlyph.CalendarFilled, Translator.KeyboardShortcuts_ModeCalendar, "⌘2"),
        new(WinoApplicationMode.Contacts, WinoIconGlyph.People, WinoIconGlyph.PeopleFilled, Translator.ContactsPage_Title, "⌘3"),
        new(WinoApplicationMode.Tasks, WinoIconGlyph.CheckmarkCircle, WinoIconGlyph.CheckmarkCircleFilled, Translator.ToDoPage_Title, "⌘4"),
        new(WinoApplicationMode.Settings, WinoIconGlyph.Settings, WinoIconGlyph.SettingsFilled, Translator.MenuSettings, "⌘,")
    ];

    /// <summary>The four app modes without Settings (iOS and iPadOS never show Settings as a mode).</summary>
    public static IReadOnlyList<WinoModeSegment> AppModes { get; } = All.Where(segment => segment.Mode != WinoApplicationMode.Settings).ToArray();
}

/// <summary>Provider glyphs of the account icon (Windows AccountIconGlyphs).</summary>
public static class WinoAccountIconGlyphs
{
    /// <summary>A tinted icon uses the mono twins of the brand logos.</summary>
    public static WinoIconGlyph ProviderGlyph(AccountIconProvider provider, bool isTinted) => provider switch
    {
        AccountIconProvider.Microsoft => isTinted ? WinoIconGlyph.MicrosoftMono : WinoIconGlyph.Microsoft,
        AccountIconProvider.Google => isTinted ? WinoIconGlyph.GoogleMono : WinoIconGlyph.Google,
        AccountIconProvider.ICloud => WinoIconGlyph.Apple,
        AccountIconProvider.Yahoo => WinoIconGlyph.Yahoo,
        _ => WinoIconGlyph.IMAP,
    };
}

/// <summary>The look of a calendar event tile (Windows CalendarItemControl).</summary>
public static class WinoCalendarTileAppearance
{
    public const double StripeWidth = 3;
    public const double CornerRadius = 2;
    public const double GlyphSize = 12;
    public const double GlyphSpacing = 6;

    /// <summary>The "show as" stripe colour as 0xRRGGBB (Windows ShowAs stripe templates).</summary>
    public static uint StripeColor(CalendarItemShowAs showAs) => showAs switch
    {
        CalendarItemShowAs.Free => 0x4CAF50,
        CalendarItemShowAs.Tentative => 0xFFC107,
        CalendarItemShowAs.OutOfOffice => 0x9C27B0,
        CalendarItemShowAs.WorkingElsewhere => 0x2196F3,
        _ => 0xF44336
    };

    /// <summary>Fill opacity: ghosts 0.2, busy tiles 0.55, multi-day month bars opaque, others 0.9; hover adds 0.08.</summary>
    public static double FillOpacity(CalendarPlacementKind kind, bool isBusy, bool isMultiDay, bool isHovered)
    {
        bool ghost = kind == CalendarPlacementKind.MultiDayGhost;
        double opacity = ghost ? 0.2 : isBusy ? 0.55 : kind == CalendarPlacementKind.MonthCell && isMultiDay ? 1 : 0.9;
        return isHovered && !ghost ? Math.Min(1, opacity + 0.08) : opacity;
    }

    /// <summary>Width the trailing repeat and multi-day glyphs take, including their gap to the text.</summary>
    public static double GlyphColumnWidth(bool isRecurring, bool isMultiDay)
    {
        int count = (isRecurring ? 1 : 0) + (isMultiDay ? 1 : 0);
        return count == 0 ? 0 : count * GlyphSize + (count - 1) * GlyphSpacing + 4;
    }

    /// <summary>Title point size: 11 on short month bars, 12 on month cells, 13 elsewhere.</summary>
    public static double TitleFontSize(CalendarPlacementKind kind, bool isMultiDay, double height)
    {
        bool compact = kind is CalendarPlacementKind.AllDay or CalendarPlacementKind.MonthCell;
        if (kind == CalendarPlacementKind.MonthCell && height < 24) return 11;
        return kind == CalendarPlacementKind.MonthCell && !isMultiDay && !compact ? 12 : 13;
    }

    /// <summary>All-day and month tiles centre one line; timed tiles stack title, time and location from the top.</summary>
    public static bool IsSingleLine(CalendarPlacementKind kind) => kind is CalendarPlacementKind.AllDay or CalendarPlacementKind.MonthCell;

    /// <summary>Timed tiles show the time and location lines from this height on.</summary>
    public const double DetailLinesMinHeight = 52;
}

/// <summary>Localized text of the native intelligence header.</summary>
public static class WinoIntelligenceHeaderText
{
    /// <summary>The status line for a processing state; empty once processed.</summary>
    public static string Subtitle(WinoIntelligenceProcessingState state) => state switch
    {
        WinoIntelligenceProcessingState.Queued => Translator.WinoIntelligence_Queued,
        WinoIntelligenceProcessingState.Processing => Translator.WinoIntelligence_Processing,
        WinoIntelligenceProcessingState.Failed => Translator.WinoIntelligence_ProcessingFailed,
        WinoIntelligenceProcessingState.Unavailable => Translator.WinoIntelligence_Unavailable,
        WinoIntelligenceProcessingState.NotProcessed => Translator.WinoIntelligence_NotProcessed,
        _ => string.Empty,
    };

    /// <summary>Process, or Retry after a failure.</summary>
    public static string ProcessTitle(WinoIntelligenceProcessingState state)
        => state == WinoIntelligenceProcessingState.Failed ? Translator.WinoIntelligence_RetryProcess : Translator.WinoIntelligence_Process;

    public static string RunTitle(WinoIntelligenceTranslateRunKind kind) => kind switch
    {
        WinoIntelligenceTranslateRunKind.ShowOriginal => Translator.WinoIntelligence_ShowOriginal,
        WinoIntelligenceTranslateRunKind.TranslateAgain => Translator.WinoIntelligence_TranslateAgain,
        _ => Translator.WinoIntelligence_Translate
    };

    /// <summary>"Translate · to English" when a target is known, else "Translate".</summary>
    public static string TranslateChipTitle(string? targetLabel)
    {
        var hint = TargetHint(targetLabel);
        return string.IsNullOrEmpty(hint) ? Translator.WinoIntelligence_Translate : $"{Translator.WinoIntelligence_Translate} · {hint}";
    }

    /// <summary>The translate entry point's accessibility label: the busy status while translating.</summary>
    public static string TranslateChipAccessibility(WinoIntelligenceHeaderState state, string? targetLabel)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.IsTranslationBusy) return state.TranslationStatusText;

        var hint = TargetHint(targetLabel);
        return string.IsNullOrEmpty(hint) ? Translator.WinoIntelligence_Translate : $"{Translator.WinoIntelligence_Translate}, {hint}";
    }

    /// <summary>The disclosure button's label: title, briefing or status, and the expansion state.</summary>
    public static string HeaderAccessibility(WinoIntelligenceHeaderState state, bool expanded)
    {
        ArgumentNullException.ThrowIfNull(state);
        var parts = new List<string> { Translator.WinoIntelligence_HeaderTitle };
        var subtitle = Subtitle(state.ProcessingState);
        if (state.HasBriefingFact) parts.Add(state.BriefingFactText);
        else if (!string.IsNullOrEmpty(subtitle)) parts.Add(subtitle);
        parts.Add(ExpansionText(expanded));
        return string.Join(", ", parts);
    }

    public static string ExpansionText(bool expanded) => expanded ? Translator.WinoIntelligence_Expanded : Translator.WinoIntelligence_Collapsed;

    public static string SummaryChipAccessibility(WinoIntelligenceFeatureState state)
        => state == WinoIntelligenceFeatureState.Busy ? Translator.WinoIntelligence_Summarizing : Translator.WinoIntelligence_Summarize;

    /// <summary>The deadline line prefers the detailed text.</summary>
    public static string Deadline(WinoIntelligenceHeaderState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return string.IsNullOrWhiteSpace(state.DeadlineDetailText) ? state.DeadlineText : state.DeadlineDetailText;
    }

    private static string TargetHint(string? targetLabel)
        => targetLabel is null ? string.Empty : string.Format(Translator.WinoIntelligence_TranslateTargetHintFormat, targetLabel);
}

/// <summary>Rules of the contact list rows.</summary>
public static class WinoContactRowRules
{
    /// <summary>The contact ViewModel properties whose change re-reads the row (null or empty means every property).</summary>
    public static bool RefreshesRow(string? propertyName)
        => propertyName is null or "" or "IsFavorite" or "Name" or "SecondaryValue" or "Categories" or "ContactPictureFileId" or "SourceContact";
}
