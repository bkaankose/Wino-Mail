using System;

namespace Wino.Messaging.Client.Mails
{
    /// <summary>
    /// When a IMailItem needs to be navigated (or selected)
    /// </summary>
    /// <param name="UniqueMailId">UniqueId of the mail to navigate.</param>
    /// <param name="ScrollToItem">Whether navigated item should be scrolled to or not..</param>
    public record MailItemNavigationRequested(Guid UniqueMailId, bool ScrollToItem = false);
}
