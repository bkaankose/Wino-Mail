using System;

namespace Wino.Messaging.Client.Mails;

/// <summary>
/// When IMAP setup dialog breadcrumb navigation requested.
/// </summary>
/// <param name="PageType">Page type to navigate.</param>
/// <param name="Parameter">Navigation parameters.</param>
public record ImapSetupNavigationRequested(Type PageType, object Parameter);
