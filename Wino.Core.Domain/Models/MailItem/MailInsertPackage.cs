using System.Collections.Generic;
using MimeKit;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Models.MailItem;

public record NewMailItemPackage(
    MailCopy Copy,
    MimeMessage Mime,
    string AssignedRemoteFolderId,
    IReadOnlyList<AccountContact> ExtractedContacts = null,
    IReadOnlyList<string> CategoryNames = null);
