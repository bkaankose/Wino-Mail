using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

public interface IWebView2RuntimeValidatorService
{
    /// <summary>
    /// Validates whether WebView2 runtime is installed and available for use.
    /// </summary>
    Task<bool> IsRuntimeAvailableAsync();
}

