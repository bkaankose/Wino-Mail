using System;

namespace Wino.Messaging.Client.Mails;

/// <summary>
/// When IMAP setup dialog requestes back breadcrumb navigation.
/// Not providing PageType will go back to previous page by doing back navigation.
/// </summary>
/// <param name="PageType">Type to go back.</param>
/// <param name="Parameter">Back parameters.</param>
public record ImapSetupBackNavigationRequested(Type PageType = null, object Parameter = null);
