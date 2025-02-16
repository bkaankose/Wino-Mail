using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

public interface IProviderService
{
    List<IProviderDetail> GetAvailableProviders();
    IProviderDetail GetProviderDetail(MailProviderType type);
}
