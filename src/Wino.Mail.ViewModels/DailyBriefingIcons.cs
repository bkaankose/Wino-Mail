using System;
using Wino.Mail.AI.Abstractions;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.ViewModels;

/// <summary>
/// WinoIcons glyphs used by the daily briefing: the panel chrome, one per smart label,
/// and one per action a card's primary button can carry.
/// </summary>
public static class DailyBriefingIcons
{
    public static readonly string Close = WinoIconGlyphs.GetGlyph(WinoIconGlyph.Dismiss);
    public static readonly string Information = WinoIconGlyphs.GetGlyph(WinoIconGlyph.Info);
    public static readonly string OpenMail = WinoIconGlyphs.GetGlyph(WinoIconGlyph.Open);
    public static readonly string Priority = WinoIconGlyphs.GetGlyph(WinoIconGlyph.Warning);
    public static readonly string Hide = WinoIconGlyphs.GetGlyph(WinoIconGlyph.EyeOff);
    public static readonly string Show = WinoIconGlyphs.GetGlyph(WinoIconGlyph.Eye);
    public static readonly string Time = WinoIconGlyphs.GetGlyph(WinoIconGlyph.Clock);
    public static readonly string Warning = WinoIconGlyphs.GetGlyph(WinoIconGlyph.AlertCircle);
    public static readonly string Visibility = Hide;

    // One per briefing action. An action Wino cannot complete still gets its own glyph, so a
    // card says what the message asks for before its label is read.
    public static readonly string Reply = WinoIconGlyphs.GetGlyph(WinoIconGlyph.Reply);
    public static readonly string Copy = WinoIconGlyphs.GetGlyph(WinoIconGlyph.Copy);
    public static readonly string Pay = WinoIconGlyphs.GetGlyph(WinoIconGlyph.Payment);
    public static readonly string Confirm = WinoIconGlyphs.GetGlyph(WinoIconGlyph.Checkmark);
    public static readonly string Calendar = WinoIconGlyphs.GetGlyph(WinoIconGlyph.Calendar);
    public static readonly string TrackShipment = WinoIconGlyphs.GetGlyph(WinoIconGlyph.Package);
    public static readonly string Review = WinoIconGlyphs.GetGlyph(WinoIconGlyph.Eye);
    public static readonly string Sign = WinoIconGlyphs.GetGlyph(WinoIconGlyph.Signature);
    public static readonly string Unsubscribe = WinoIconGlyphs.GetGlyph(WinoIconGlyph.PersonDelete);
    public static readonly string Security = WinoIconGlyphs.GetGlyph(WinoIconGlyph.LockClosed);

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
        MailSmartLabel.Important => WinoIconGlyphs.GetGlyph(WinoIconGlyph.StarFilled),
        MailSmartLabel.Action => WinoIconGlyphs.GetGlyph(WinoIconGlyph.Flash),
        MailSmartLabel.Finance => WinoIconGlyphs.GetGlyph(WinoIconGlyph.Payment),
        MailSmartLabel.Travel => WinoIconGlyphs.GetGlyph(WinoIconGlyph.Airplane),
        MailSmartLabel.Social => WinoIconGlyphs.GetGlyph(WinoIconGlyph.People),
        MailSmartLabel.Newsletter => WinoIconGlyphs.GetGlyph(WinoIconGlyph.News),
        MailSmartLabel.Receipt => WinoIconGlyphs.GetGlyph(WinoIconGlyph.Cart),
        MailSmartLabel.Security => WinoIconGlyphs.GetGlyph(WinoIconGlyph.LockClosed),
        MailSmartLabel.Shipping => WinoIconGlyphs.GetGlyph(WinoIconGlyph.Package),
        _ => Information,
    };

    /// <summary>Glyph for a label name as stored locally, lowercase.</summary>
    public static string Label(string label)
        => Enum.TryParse<MailSmartLabel>(label, ignoreCase: true, out var parsed) ? Label(parsed) : Information;
}
