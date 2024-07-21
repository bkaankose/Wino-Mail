using System.Collections.Generic;
using Wino.Domain.Enums;

namespace Wino.Domain.Interfaces
{
    public interface IProviderService
    {
        List<IProviderDetail> GetProviderDetails();
        IProviderDetail GetProviderDetail(MailProviderType type);
    }
}
