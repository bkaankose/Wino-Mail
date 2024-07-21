using Wino.Domain.Entities;

namespace Wino.Domain.Interfaces
{
    public interface IAccountProviderDetails
    {
        MailAccount Account { get; set; }
        bool AutoExtend { get; set; }
        IProviderDetail ProviderDetail { get; set; }
    }
}
