using System.Threading.Tasks;

namespace Wino.Core.Integration
{
    public abstract class BaseCalendarIntegrator<TNativeRequestType, TCalendarEventType>
    {
        public abstract Task<TCalendarEventType> CreateCalendarEventAsync(TNativeRequestType request);
    }
}
