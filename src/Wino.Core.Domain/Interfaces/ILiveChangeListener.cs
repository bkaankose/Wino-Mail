using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>A synchronizer that keeps a connection open to hear about server changes, such as IMAP IDLE or Exchange notifications.</summary>
public interface ILiveChangeListener
{
    Task StartListeningAsync();

    /// <summary>Stops listening and releases connections while the account needs attention.</summary>
    Task SuspendListeningAsync();
}
