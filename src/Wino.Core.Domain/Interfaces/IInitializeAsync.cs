using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// An interface that all startup services must implement.
/// </summary>
public interface IInitializeAsync
{
    Task InitializeAsync();
}
