using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Contacts;

public record ContactOperationPreparationRequest(
    ContactSynchronizerOperation Operation,
    AccountContact Contact,
    AccountContact OriginalContact = null,
    byte[] Photo = null);
