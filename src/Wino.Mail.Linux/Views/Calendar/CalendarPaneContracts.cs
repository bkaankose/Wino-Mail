namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>A view shown over the whole calendar page (the event composer) that asks the page to close it.</summary>
internal interface ICalendarCoverView
{
    event EventHandler? CloseRequested;
}
