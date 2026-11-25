using System;
using MimeKit;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Services.Models.MailItem;

/// <summary>
/// A message moved from another account: the exact MIME of the original and the state it keeps in the target folder.
/// It is never sent; the target stores it as it is, like an IMAP APPEND.
/// </summary>
public sealed record MailImportPackage(MimeMessage Mime, MailItemFolder TargetFolder, bool IsRead, bool IsFlagged, DateTime ReceivedDateUtc);
