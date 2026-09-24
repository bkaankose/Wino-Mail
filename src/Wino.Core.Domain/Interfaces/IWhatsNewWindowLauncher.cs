using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>Opens the What's New window, or brings the open one to the front.</summary>
public interface IWhatsNewWindowLauncher
{
    Task ShowAsync();
}
