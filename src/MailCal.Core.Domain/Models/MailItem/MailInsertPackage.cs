using System.Collections.Generic;
using MimeKit;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Entities.Shared;

namespace MailCal.Core.Domain.Models.MailItem;

public record NewMailItemPackage(
    MailCopy Copy,
    MimeMessage Mime,
    string AssignedRemoteFolderId,
    IReadOnlyList<AccountContact> ExtractedContacts = null,
    IReadOnlyList<string> CategoryNames = null,
    bool SuppressUiChange = false);
