﻿using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Core.UWP.Controls;

public enum WinoIconGlyph
{
    None,
    NewMail,
    Google,
    Microsoft,
    CustomServer,
    Archive,
    UnArchive,
    Reply,
    ReplyAll,
    LightEditor,
    DarkEditor,
    Delete,
    Move,
    Mail,
    Draft,
    Flag,
    ClearFlag,
    Folder,
    Forward,
    Inbox,
    MarkRead,
    MarkUnread,
    Send,
    Save,
    Sync,
    MultiSelect,
    Zoom,
    Pin,
    UnPin,
    Ignore,
    Star,
    CreateFolder,
    More,
    Find,
    SpecialFolderInbox,
    SpecialFolderStarred,
    SpecialFolderImportant,
    SpecialFolderSent,
    SpecialFolderDraft,
    SpecialFolderArchive,
    SpecialFolderDeleted,
    SpecialFolderJunk,
    SpecialFolderChat,
    SpecialFolderCategory,
    SpecialFolderUnread,
    SpecialFolderForums,
    SpecialFolderUpdated,
    SpecialFolderPersonal,
    SpecialFolderPromotions,
    SpecialFolderSocial,
    SpecialFolderOther,
    SpecialFolderMore,
    TurnOfNotifications,
    EmptyFolder,
    Rename,
    DontSync,
    Attachment,
    SortTextDesc,
    SortLinesDesc,
    Certificate,
    OpenInNewWindow,
    Blocked,
    Message,
    New,
    IMAP,
    Print,
    Calendar,
    CalendarToday,
    CalendarDay,
    CalendarWeek,
    CalendarWorkWeek,
    CalendarMonth,
    CalendarYear,
    WeatherBlow,
    WeatherCloudy,
    WeatherSunny,
    WeatherRainy,
    WeatherSnowy,
    WeatherSnowShowerAtNight,
    WeatherThunderstorm,
    CalendarEventRepeat,
    CalendarEventMuiltiDay,
    CalendarError,
    Reminder,
    CalendarAttendee,
    CalendarAttendees,
    CalendarSync,
    EventRespond,
    EventAccept,
    EventTentative,
    EventDecline,
    EventReminder,
    EventEditSeries,
    EventJoinOnline,
    ViewMessageSource,
    Apple,
    Yahoo
}

public partial class WinoFontIcon : FontIcon
{
    public WinoIconGlyph Icon
    {
        get { return (WinoIconGlyph)GetValue(IconProperty); }
        set { SetValue(IconProperty, value); }
    }

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(WinoIconGlyph), typeof(WinoFontIcon), new PropertyMetadata(WinoIconGlyph.Flag, OnIconChanged));

    public WinoFontIcon()
    {
        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("/Wino.Core.WinUI/Assets/WinoIcons.ttf#WinoIcons");
        FontSize = 32;
    }

    private static void OnIconChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is WinoFontIcon fontIcon)
        {
            fontIcon.UpdateGlyph();
        }
    }

    private void UpdateGlyph()
    {
        Glyph = ControlConstants.WinoIconFontDictionary[Icon];
    }
}
