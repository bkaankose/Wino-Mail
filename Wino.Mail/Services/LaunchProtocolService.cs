using System.Collections.Specialized;
using Wino.Domain.Interfaces;

namespace Wino.Services
{
    public class LaunchProtocolService : ILaunchProtocolService
    {
        public object LaunchParameter { get; set; }
        public NameValueCollection MailtoParameters { get; set; }
    }
}
