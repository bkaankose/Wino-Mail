using System.Collections.Specialized;

namespace Wino.Core.Domain.Interfaces
{
    public interface ILaunchProtocolService
    {
        object LaunchParameter { get; set; }
        NameValueCollection MailtoParameters { get; set; }
    }
}
