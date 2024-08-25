using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Launch;

namespace Wino.Core.Services;

public class LaunchProtocolService : ILaunchProtocolService
{
    public object LaunchParameter { get; set; }
    public MailToUri MailToUri { get; set; }
}
