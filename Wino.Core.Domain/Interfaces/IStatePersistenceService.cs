using System;
using System.ComponentModel;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

public interface IStatePersistanceService : INotifyPropertyChanged
{
    event EventHandler<string> StatePropertyChanged;

    /// <summary>
    /// True when there is an active renderer for selected mail.
    /// </summary>
    bool IsReadingMail { get; set; }

    /// <summary>
    /// Shell's app bar title string.
    /// </summary>
    string CoreWindowTitle { get; set; }

    /// <summary>
    /// When only reader page is visible in small sized window.
    /// </summary>
    bool IsReaderNarrowed { get; set; }

    /// <summary>
    /// Should display back button on the shell title bar.
    /// </summary>
    bool IsBackButtonVisible { get; }


    /// <summary>
    /// Setting: Opened pane length for the navigation view.
    /// </summary>
    double OpenPaneLength { get; set; }

    /// <summary>
    /// Whether the mail rendering page should be shifted from top to adjust the design
    /// for standalone EML viewer or not.
    /// </summary>
    bool ShouldShiftMailRenderingDesign { get; set; }

    /// <summary>
    /// Setting: Mail list pane length for listing mails.
    /// </summary>
    double MailListPaneLength { get; set; }

    /// <summary>
    /// Setting: Calendar display type.
    /// </summary>
    CalendarDisplayType CalendarDisplayType { get; set; }

    /// <summary>
    /// Setting: Calendar display count for the day view.
    /// </summary>
    int DayDisplayCount { get; set; }
}
