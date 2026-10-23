using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Models.Contacts;

public record ContactOperationPreparationRequest(
    ContactSynchronizerOperation Operation,
    AccountContact Contact,
    AccountContact OriginalContact = null,
    byte[] Photo = null);
