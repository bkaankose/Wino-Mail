using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Models.Contacts;

public record ContactSynchronizationBatch(
    IReadOnlyList<AccountContact> Upserts,
    IReadOnlyList<string> DeletedRemoteIds,
    string NextDeltaToken);
