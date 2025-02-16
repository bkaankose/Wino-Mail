using Wino.Core.Domain.Entities.Shared;

namespace Wino.Messaging.Client.Mails;

/// <summary>
/// When user asked to dismiss IMAP setup dialog.
/// </summary>
/// <param name="CompletedServerInformation"> Validated server information that is ready to be saved to database. </param>
public record ImapSetupDismissRequested(CustomServerInformation CompletedServerInformation = null);
