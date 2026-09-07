using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

public interface IWinoPendingCheckoutStore
{
    void Save(Guid accountId, WinoAddOnProductType product);
    WinoAddOnProductType? Get(Guid accountId);
    void Clear(Guid accountId);
}
