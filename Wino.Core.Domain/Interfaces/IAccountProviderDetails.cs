using Wino.Core.Domain.Entities;

namespace Wino.Core.Domain.Interfaces
{
    public interface IAccountProviderDetails
    {
        MailAccount Account { get; set; }
        bool AutoExtend { get; set; }
        IProviderDetail ProviderDetail { get; set; }
    }
}
