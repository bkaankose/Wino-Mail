using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces
{
    public interface IProviderService
    {
        List<IProviderDetail> GetProviderDetails();
        IProviderDetail GetProviderDetail(MailProviderType type);
    }
}
