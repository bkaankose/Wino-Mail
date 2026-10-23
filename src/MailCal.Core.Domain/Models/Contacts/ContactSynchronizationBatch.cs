using System.Collections.Generic;
using MailCal.Core.Domain.Entities.Shared;

namespace MailCal.Core.Domain.Models.Contacts;

public record ContactSynchronizationBatch(
    IReadOnlyList<AccountContact> Upserts,
    IReadOnlyList<string> DeletedRemoteIds,
    string NextDeltaToken);
