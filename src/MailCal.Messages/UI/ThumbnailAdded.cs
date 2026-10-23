using MailCal.Core.Domain.Interfaces;

namespace MailCal.Messaging.UI;
public record ThumbnailAdded(string Email): IUIMessage;
