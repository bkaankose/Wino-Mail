using Wino.Core.Domain.Models.Launch;

namespace Wino.Core.Domain.Interfaces;

public interface ILaunchProtocolService
{
    /// <summary>
    /// Used to handle toasts.
    /// </summary>
    object LaunchParameter { get; set; }

    /// <summary>
    /// Used to handle mailto links.
    /// </summary>
    MailToUri MailToUri { get; set; }
}
