using System;

namespace Wino.Core.Messages.Mails
{
    /// <summary>
    /// When IMAP setup dialog requestes back breadcrumb navigation.
    /// </summary>
    /// <param name="PageType">Type to go back.</param>
    /// <param name="Parameter">Back parameters.</param>
    public record ImapSetupBackNavigationRequested(Type PageType, object Parameter);
}
