using Wino.Domain.Enums;

namespace Wino.Domain.Interfaces
{
    public interface IThreadingStrategyProvider
    {
        /// <summary>
        /// Returns corresponding threading strategy that applies to given provider type.
        /// </summary>
        /// <param name="mailProviderType">Provider type.</param>
        IThreadingStrategy GetStrategy(MailProviderType mailProviderType);
    }
}
