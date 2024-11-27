using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Synchronizers
{
    public abstract class BaseCalendarSynchronizer<TBaseRequest, TMessageType> : BaseSynchronizer<TBaseRequest>, IBaseCalendarSynchronizer
    {
        protected BaseCalendarSynchronizer(MailAccount account) : base(account)
        {
        }
    }
}
