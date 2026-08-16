using Wino.Mail.AI.Abstractions;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Segoe Fluent glyphs used by the daily briefing. Keeping them here prevents action and category
/// presentation code from accumulating hard-coded font values.
/// </summary>
public static class DailyBriefingIcons
{
    public const string Accept = "\uE8FB";
    public const string AddToCalendar = "\uE787";
    public const string Approve = "\uE73E";
    public const string Cancel = "\uE711";
    public const string ChangePassword = "\uE72E";
    public const string CheckIn = "\uE709";
    public const string Complete = "\uE930";
    public const string Confirm = "\uE73E";
    public const string Copy = "\uE8C8";
    public const string Decline = "\uE711";
    public const string Download = "\uE896";
    public const string FollowUp = "\uE823";
    public const string Information = "\uE946";
    public const string Link = "\uE71B";
    public const string Lock = "\uE72E";
    public const string OpenMail = "\uE8A7";
    public const string Pay = "\uE8C7";
    public const string Renew = "\uE72C";
    public const string Reply = "\uE97A";
    public const string ReportPhishing = "\uE730";
    public const string Reschedule = "\uE787";
    public const string Review = "\uE890";
    public const string Sign = "\uE70F";
    public const string Submit = "\uE8A7";
    public const string Time = "\uE823";
    public const string TrackShipment = "\uE7B8";
    public const string Unsubscribe = "\uE711";
    public const string Verify = "\uE73E";
    public const string ViewDocument = "\uE8A5";
    public const string ViewItinerary = "\uE709";
    public const string ViewOrder = "\uE7B8";
    public const string ViewReservation = "\uE8D2";
    public const string Warning = "\uE783";
    public const string Close = "\uE8BB";
    public const string Priority = "\uE7BA";
    public const string Hide = "\uE7B3";
    public const string Show = "\uE7B2";
    public const string Visibility = Hide;

    /// <summary>Bindable glyph values used directly by the panel XAML.</summary>
    public static string CloseGlyph => Close;
    public static string InformationGlyph => Information;
    public static string LockGlyph => Lock;
    public static string OpenMailGlyph => OpenMail;
    public static string PriorityGlyph => Priority;
    public static string HideGlyph => Hide;
    public static string ShowGlyph => Show;
    public static string VisibilityGlyph => Visibility;
    public static string TimeGlyph => Time;
    public static string WarningGlyph => Warning;

    /// <summary>Gets the glyph for a briefing category tile.</summary>
    public static string Category(BriefingFactCategory category) => category switch
    {
        BriefingFactCategory.ActionRequired => "\uE945",
        BriefingFactCategory.Waiting => "\uE823",
        BriefingFactCategory.Security => "\uE72E",
        BriefingFactCategory.Finance => "\uE8C7",
        BriefingFactCategory.Travel => "\uE709",
        BriefingFactCategory.Personal => "\uE77B",
        _ => "\uE946",
    };
}
