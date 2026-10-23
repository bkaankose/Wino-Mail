using System.Threading.Tasks;

namespace MailCal.Core.Domain.Interfaces;

/// <summary>
/// An interface that all startup services must implement.
/// </summary>
public interface IInitializeAsync
{
    Task InitializeAsync();
}
