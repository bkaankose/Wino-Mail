using System;
using Wino.Mail.AI.Abstractions;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Segoe Fluent glyphs used by the daily briefing.
/// The set is deliberately small now: the briefing shows labels, priority and a headline,
/// so there are no per-action or per-category glyphs left to map.
/// </summary>
public static class DailyBriefingIcons
{
    public const string Close = "";
    public const string Information = "";
    public const string OpenMail = "";
    public const string Priority = "";
    public const string Hide = "";
    public const string Show = "";
    public const string Time = "";
    public const string Warning = "";
    public const string Visibility = Hide;

    /// <summary>Bindable glyph values used directly by the panel XAML.</summary>
    public static string CloseGlyph => Close;
    public static string InformationGlyph => Information;
    public static string OpenMailGlyph => OpenMail;
    public static string PriorityGlyph => Priority;
    public static string HideGlyph => Hide;
    public static string ShowGlyph => Show;
    public static string VisibilityGlyph => Visibility;
    public static string TimeGlyph => Time;
    public static string WarningGlyph => Warning;

    /// <summary>Glyph for one smart-label chip.</summary>
    public static string Label(MailSmartLabel label) => label switch
    {
        MailSmartLabel.Important => "",
        MailSmartLabel.Action => "",
        MailSmartLabel.Finance => "",
        MailSmartLabel.Travel => "",
        MailSmartLabel.Social => "",
        MailSmartLabel.Newsletter => "",
        MailSmartLabel.Receipt => "",
        _ => Information,
    };

    /// <summary>Glyph for a label name as stored locally, lowercase.</summary>
    public static string Label(string label)
        => Enum.TryParse<MailSmartLabel>(label, ignoreCase: true, out var parsed) ? Label(parsed) : Information;
}
