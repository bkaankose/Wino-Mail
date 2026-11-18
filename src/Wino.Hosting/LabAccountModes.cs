using Wino.Core.Domain.Enums;

namespace Wino.Hosting;

/// <summary>
/// The modes <see cref="WinoHost.AddImapAccountAsync"/> turns on besides mail, as the setup page's capability switches
/// set them. Contacts and tasks go through the lab server's DAV service unless their <c>Through…Dav</c> switch is off.
/// </summary>
public sealed record LabAccountModes
{
    public static LabAccountModes MailOnly { get; } = new();

    /// <summary>Calendar, contacts and tasks all through the lab server's CalDAV and CardDAV service.</summary>
    public static LabAccountModes AllThroughDav { get; } = new() { Calendar = ImapCalendarSupportMode.CalDav, Contacts = true, Tasks = true };

    /// <summary>A calendar, contacts and tasks kept only on this device.</summary>
    public static LabAccountModes AllLocal { get; } = new()
    {
        Calendar = ImapCalendarSupportMode.LocalOnly,
        Contacts = true,
        ContactsThroughDav = false,
        Tasks = true,
        TasksThroughDav = false
    };

    public ImapCalendarSupportMode Calendar { get; init; } = ImapCalendarSupportMode.Disabled;
    public bool Contacts { get; init; }
    public bool ContactsThroughDav { get; init; } = true;
    public bool Tasks { get; init; }
    public bool TasksThroughDav { get; init; } = true;

    internal bool UsesDav => Calendar == ImapCalendarSupportMode.CalDav || (Contacts && ContactsThroughDav) || (Tasks && TasksThroughDav);
}
