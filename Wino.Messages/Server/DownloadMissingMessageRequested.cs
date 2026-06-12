using System;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;

namespace Wino.Messaging.Server;

/// <summary>
/// Message to download a missing message.
/// Sent from client to server.
/// </summary>
/// <param name="AccountId">Account id for corresponding synchronizer.</param>
/// <param name="MailCopyId">Mail copy id to download.</param>
public record DownloadMissingMessageRequested(Guid AccountId, MailCopy MailItem) : IClientMessage;
