namespace Wino.Core.Domain.Enums;

/// <summary>
/// How much of a message is shown on its notification.
/// </summary>
public enum MailNotificationContent
{
    SenderSubjectPreview = 0,
    SenderSubject = 1,
    SenderOnly = 2,
    Nothing = 3
}
