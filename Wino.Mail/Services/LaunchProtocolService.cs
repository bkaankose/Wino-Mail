using System.Collections.Specialized;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.UWP.Services
{
    public class LaunchProtocolService : ILaunchProtocolService
    {
        public object LaunchParameter { get; set; }
        public NameValueCollection MailtoParameters { get; set; }
    }
}
