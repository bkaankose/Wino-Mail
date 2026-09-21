using System;
using Wino.Mail.AI.Abstractions;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Segoe Fluent glyphs used by the daily briefing: the panel chrome, one per smart label,
/// and one per action a card's primary button can carry.
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

    // One per briefing action. An action Wino cannot complete still gets its own glyph, so a
    // card says what the message asks for before its label is read.
    public const string Reply = "";
    public const string Copy = "";
    public const string Pay = "";
    public const string Confirm = "";
    public const string Calendar = "";
    public const string TrackShipment = "";
    public const string Review = "";
    public const string Sign = "";
    public const string Unsubscribe = "";
    public const string Security = "";

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
        MailSmartLabel.ActionRequired => "",
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
