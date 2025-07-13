using System;
using System.ComponentModel;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Reader;

namespace Wino.Core.Domain.Interfaces;

public interface IPreferencesService: INotifyPropertyChanged
{
    /// <summary>
    /// When any of the preferences are changed.
    /// </summary>
    event EventHandler<string> PreferenceChanged;

    #region Common

    /// <summary>
    /// Setting: Whether logs are enabled or not.
    /// </summary>
    bool IsLoggingEnabled { get; set; }

    /// <summary>
    /// Setting: Display language for the application.
    /// </summary>
    AppLanguage CurrentLanguage { get; set; }

    /// <summary>
    /// Setting: Whether the navigation pane is opened on the last session or not.
    /// </summary>
    bool IsNavigationPaneOpened { get; set; }

    /// <summary>
    /// Setting: Gets or sets what should happen to server app when the client is terminated.
    /// </summary>
    ServerBackgroundMode ServerTerminationBehavior { get; set; }

    /// <summary>
    /// Setting: Preferred time format for mail or calendar header display.
    /// </summary>
    bool Prefer24HourTimeFormat { get; set; }

    /// <summary>
    /// Diagnostic ID for the application.
    /// Changes per-install.
    /// </summary>
    string DiagnosticId { get; set; }

    /// <summary>
    /// Setting: Defines the user's preference of default search mode in mail list.
    /// Local search will still offer online search at the end of local search results.
    /// </summary>
    SearchMode DefaultSearchMode { get; set; }

    /// <summary>
    /// Setting: Interval in minutes for background email synchronization.
    /// </summary>
    int EmailSyncIntervalMinutes { get; set; }

    #endregion

    #region Mail

    /// <summary>
    /// Setting: For changing the mail display container mode.
    /// </summary>
    MailListDisplayMode MailItemDisplayMode { get; set; }

    /// <summary>
    /// Setting: Marking the item as read preference mode.
    /// </summary>
    MailMarkAsOption MarkAsPreference { get; set; }

    /// <summary>
    /// Setting: How many seconds should be waited on rendering page to mark item as read.
    /// </summary>
    int MarkAsDelay { get; set; }

    /// <summary>
    /// Setting: Ask comfirmation from the user during permanent delete.
    /// </summary>
    bool IsHardDeleteProtectionEnabled { get; set; }

    /// <summary>
    /// Setting: Thread mails into conversations.
    /// </summary>
    bool IsThreadingEnabled { get; set; }

    /// <summary>
    /// Setting: Show sender pictures in mail list.
    /// </summary>
    bool IsShowSenderPicturesEnabled { get; set; }

    /// <summary>
    /// Setting: Show preview text in mail list.
    /// </summary>
    bool IsShowPreviewEnabled { get; set; }

    /// <summary>
    /// Setting: Enable/disable semantic zoom on clicking date headers.
    /// </summary>
    bool IsSemanticZoomEnabled { get; set; }

    /// <summary>
    /// Setting: Set whether 'img' tags in rendered HTMLs should be removed.
    /// </summary>
    bool RenderImages { get; set; }

    /// <summary>
    /// Setting: Set whether 'style' tags in rendered HTMls should be removed.
    /// </summary>
    bool RenderStyles { get; set; }

    /// <summary>
    /// Setting: Set whether plaintext links should be automatically converted to clickable links.
    /// </summary>
    bool RenderPlaintextLinks { get; set; }

    /// <summary>
    /// Gets the preferred rendering options for HTML rendering.
    /// </summary>
    MailRenderingOptions GetRenderingOptions();

    /// <summary>
    /// Setting: Swipe mail operation when mails are swiped to right.
    /// </summary>
    MailOperation RightSwipeOperation { get; set; }

    /// <summary>
    /// Setting: Swipe mail operation when mails are swiped to left.
    /// </summary>
    MailOperation LeftSwipeOperation { get; set; }

    /// <summary>
    /// Setting: Whether hover actions on mail pointer hover is enabled or not.
    /// </summary>
    bool IsHoverActionsEnabled { get; set; }

    /// <summary>
    /// Setting: Hover action on the left when the mail is hovered over.
    /// </summary>
    MailOperation LeftHoverAction { get; set; }

    /// <summary>
    /// Setting: Hover action on the center when the mail is hovered over.
    /// </summary>
    MailOperation CenterHoverAction { get; set; }

    /// <summary>
    /// Setting: Hover action on the right when the mail is hovered over.
    /// </summary>
    MailOperation RightHoverAction { get; set; }

    /// <summary>
    /// Setting: Whether Mailkit Protocol Logger is enabled for ImapTestService or not.
    /// </summary>
    bool IsMailkitProtocolLoggerEnabled { get; set; }

    /// <summary>
    /// Setting: Which entity id (merged account or folder) should be expanded automatically on startup.
    /// </summary>
    Guid? StartupEntityId { get; set; }



    /// <summary>
    /// Setting: Display font for the mail reader.
    /// </summary>
    string ReaderFont { get; set; }

    /// <summary>
    /// Setting: Font size for the mail reader.
    /// </summary>
    int ReaderFontSize { get; set; }

    /// <summary>
    /// Setting: Display font for the mail composer.
    /// </summary>
    string ComposerFont { get; set; }

    /// <summary>
    /// Setting: Font size for the mail composer.
    /// </summary>
    int ComposerFontSize { get; set; }



    /// <summary>
    /// Setting: Whether the next item should be automatically selected once the current item is moved or removed.
    /// </summary>
    bool AutoSelectNextItem { get; set; }

    /// <summary>
    /// Setting: Whether the mail list action bar is enabled or not.
    /// </summary>
    bool IsMailListActionBarEnabled { get; set; }

    /// <summary>
    /// Setting: Whether the mail rendering page will show the action labels
    /// </summary>
    bool IsShowActionLabelsEnabled { get; set; }

    /// <summary>
    /// Setting: Enable/disable Gravatar for sender avatars.
    /// </summary>
    bool IsGravatarEnabled { get; set; }

    /// <summary>
    /// Setting: Enable/disable Favicon for sender avatars.
    /// </summary>
    bool IsFaviconEnabled { get; set; }

    #endregion

    #region Calendar

    DayOfWeek FirstDayOfWeek { get; set; }
    TimeSpan WorkingHourStart { get; set; }
    TimeSpan WorkingHourEnd { get; set; }
    DayOfWeek WorkingDayStart { get; set; }
    DayOfWeek WorkingDayEnd { get; set; }
    double HourHeight { get; set; }


    CalendarSettings GetCurrentCalendarSettings();

    #endregion
}
